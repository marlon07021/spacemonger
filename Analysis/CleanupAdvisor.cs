using System.Diagnostics;

namespace SpaceMonger.Analysis;

public enum SuggestionSource { Rule, Model, User }

public sealed record CleanupItem(Node Node, FolderClass Class, Safety Safety, double Confidence, SuggestionSource Source, string Reason);

public sealed class CleanupResult
{
    public List<CleanupItem> Items { get; init; } = [];
    public string ModelInfo { get; init; } = "";
    public TimeSpan Elapsed { get; init; }
    public long Reclaimable => Items.Sum(i => i.Node.Size);
}

/// <summary>Runs rules + classifier over a scanned tree and returns non-overlapping cleanup suggestions.</summary>
public static class CleanupAdvisor
{
    private const long MinFolderSize = 1L << 20;       // ignore folders under 1 MB
    private const float ModelThreshold = 0.75f;
    private const long ModelMinSize = 50L << 20;       // model suggestions below 50 MB are mostly noise

    public static CleanupResult Analyze(Node root, LabelStore labels, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var stats = FolderFeatures.Compute(root, MinFolderSize, ct);

        // 1. Rules and user labels.
        var dirs = Node.Directories(root);
        var contexts = FolderFeatures.Contexts(dirs);
        var verdicts = new Dictionary<Node, (Verdict v, SuggestionSource src)>();
        var paths = new Dictionary<Node, string>();
        var labelled = new List<FolderClassifier.Sample>();
        var userLabelled = new List<FolderClassifier.Sample>();
        var unlabelled = new List<Node>();
        foreach (var d in dirs)
        {
            if (!stats.TryGetValue(d, out var s) || d.Size < MinFolderSize) continue;
            string path = d.FullPath;
            string lower = path.ToLowerInvariant();
            paths[d] = lower;

            if (labels.Get(path) is { } userClass)
            {
                verdicts[d] = (new Verdict(userClass, userClass == FolderClass.UserKeep ? Safety.Caution : Safety.Review, "Labelled by you"), SuggestionSource.User);
                userLabelled.Add(new(FolderFeatures.Text(d), FolderFeatures.Vector(d, s, lower, contexts[d]), userClass.ToString()));
            }
            else if (CleanupRules.Classify(d, lower, s) is { } v)
            {
                verdicts[d] = (v, SuggestionSource.Rule);
                labelled.Add(new(FolderFeatures.Text(d), FolderFeatures.Vector(d, s, lower, contexts[d]), v.Class.ToString()));
            }
            else unlabelled.Add(d);
        }
        ct.ThrowIfCancellationRequested();

        // 2. Classifier for everything the rules didn't decide.
        var classifier = new FolderClassifier();
        var modelHits = new Dictionary<Node, (FolderClass cls, float conf)>();
        if (classifier.Train(labelled, userLabelled, ct))
        {
            // Guardrails: never auto-suggest protected areas, anything inside version control
            // metadata, folders where >10% of the files are source/documents/media (deleting them
            // would destroy real work even if most bytes are junk, e.g. in-source .obj files), or
            // anything inside a git repo that .gitignore doesn't exclude (tracked = hand-made).
            var gitIgnore = new GitIgnore();
            var candidates = unlabelled.Where(d =>
                d.Size >= ModelMinSize &&
                !CleanupRules.IsProtected(paths[d]) &&
                !paths[d].Contains(@"\.git\") &&
                stats[d].PreciousFileShare <= 0.10 &&
                (!(contexts[d].InsideRepo || contexts[d].IsRepoRoot) || gitIgnore.IsIgnored(d))).ToList();
            var samples = candidates.Select(d => new FolderClassifier.Sample(FolderFeatures.Text(d),
                FolderFeatures.Vector(d, stats[d], paths[d], contexts[d]), null)).ToList();
            var predictions = classifier.Predict(samples);
            for (int i = 0; i < predictions.Length; i++)
            {
                var (label, conf) = predictions[i];
                if (conf >= ModelThreshold && Enum.TryParse<FolderClass>(label, out var cls) && CleanupRules.IsReclaimable(cls))
                    modelHits[candidates[i]] = (cls, conf);
            }
        }
        ct.ThrowIfCancellationRequested();

        // 3. Pick the topmost reclaimable folders. Rule/user verdicts win; model hits may not
        //    overlap them, and nothing is suggested inside a folder the user marked "keep".
        var items = new List<CleanupItem>();
        var chosen = new HashSet<Node>();
        var hasChosenBelow = new HashSet<Node>();
        bool UnderChosenOrKept(Node d)
        {
            for (var p = d.Parent; p != null; p = p.Parent)
                if (chosen.Contains(p) || (verdicts.TryGetValue(p, out var pv) && pv.v.Class == FolderClass.UserKeep)) return true;
            return false;
        }
        void Choose(Node d, CleanupItem item)
        {
            chosen.Add(d);
            items.Add(item);
            for (var p = d.Parent; p != null; p = p.Parent) hasChosenBelow.Add(p);
        }

        foreach (var d in dirs)
        {
            if (verdicts.TryGetValue(d, out var vs) && CleanupRules.IsReclaimable(vs.v.Class) && !UnderChosenOrKept(d))
                Choose(d, new CleanupItem(d, vs.v.Class, vs.v.Safety, 1.0, vs.src, vs.v.Reason));
        }
        foreach (var d in dirs)
        {
            if (!modelHits.TryGetValue(d, out var mh) || hasChosenBelow.Contains(d) || chosen.Contains(d) || UnderChosenOrKept(d)) continue;
            Choose(d, new CleanupItem(d, mh.cls, Safety.Review, mh.conf, SuggestionSource.Model,
                $"Looks like {Describe(mh.cls)} (model, {mh.conf:P0} confident)"));
        }

        items.Sort((a, b) => b.Node.Size.CompareTo(a.Node.Size));
        return new CleanupResult { Items = items, ModelInfo = classifier.Info, Elapsed = sw.Elapsed };
    }

    public static string Describe(FolderClass c) => c switch
    {
        FolderClass.Cache => "a cache",
        FolderClass.Temp => "temporary files",
        FolderClass.BuildOutput => "build output",
        FolderClass.Dependencies => "downloaded dependencies",
        FolderClass.Logs => "logs/dumps",
        FolderClass.Installers => "installers/upgrade leftovers",
        FolderClass.RecycleBin => "Recycle Bin",
        _ => c.ToString(),
    };
}
