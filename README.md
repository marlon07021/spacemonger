# SpaceMonger

A fast Windows disk-space explorer. It scans a drive or folder and draws it as a nested **treemap**:
every rectangle is a folder or file, sized by how many bytes it uses. It also figures out what you
can safely delete.

Built with .NET 8 and WinForms. No admin rights needed, no telemetry, and nothing leaves your machine.

---

## A confession from the "author"

I vibecoded this. All of it.

I typed "make a filesystem explorer that shows folder sizes with colors" and an AI wrote a parallel
Win32 directory walker, a raw NTFS Master File Table parser, a squarified treemap rasterizer that
writes straight into a GDI DIB section, and an ML.NET classifier that trains itself on my own
`node_modules` folders. It also found and fixed its own struct-packing bug.

My contributions were:

- choosing "Nested treemap (Recommended)" from a multiple-choice menu,
- misspelling a product name I'm still not sure exists ("nucleuz"),
- asking for this README.

I have been writing code for years, and I now feel about as useful as a screen door on a submarine.
If this tool finds 64 GB of junk on your disk, please know that it could have found me too.

If you're reading the source to learn how it works: same. 🫠

*(For the record, the AI would like to point out that deciding what to build, choosing the
trade-offs and noticing what's wrong are also engineering. The author remains unconvinced.)*

---

## Features

### Treemap
- **Nested squarified layout.** Folders contain their subfolders and files, and each folder has a header with its name and size.
- **Three color modes:**
  - **Depth.** Hue shows nesting level and brightness shows size on a log scale, so big folders are bright.
  - **File type.** Each category (video, code, archives, build artifacts…) has its own color.
  - **Age.** Last-modified date, from green (recent) to red (5+ years old).
- **Navigation.** Double-click to zoom in. Use the mouse wheel to zoom toward or away from the pointer, and Backspace to go up a level. The breadcrumb bar is clickable.
- **Right-click menu.** Open, Show in Explorer, Copy path, and Delete to Recycle Bin (always asks first).
- **Live updates.** The map fills in while the scan is running.

### Folder tree and search
- **Folder tree.** Explorer-style, largest items first, with a bar showing each item's share of its parent. It stays in sync with the map both ways.
- **Global search** (Ctrl+F). Unlocks once the scan finishes and the search index is built (≈0.85 s for 5.6M items). Typical queries take 10–60 ms. Results are listed largest first, and you can limit them to the current view.

  | Query | Finds |
  |---|---|
  | `report 2024` | names containing both words |
  | `*.mp4`, `IMG_????.jpg` | wildcards |
  | `ext:iso,vhdx` | by extension |
  | `type:video` | by category (video, audio, image, document, archive, code, …) |
  | `size:>1gb`, `size:<10k` | by size |
  | `age:>2y`, `age:<7d` | by last-modified age |
  | `is:folder`, `is:file` | by kind |

  Combine them freely: `type:video size:>500mb age:>1y`.

### Two scan engines (switch in Settings)
| Engine | Needs | Notes |
|---|---|---|
| **Win32 parallel** | nothing | Multi-threaded `FindFirstFileEx`. Scanned 317k files in about 1.3 s in testing. |
| **NTFS MFT** | admin + NTFS | Reads the Master File Table directly, like WizTree. Falls back to Win32 automatically when it can't run. |

Junctions and symlinks are shown but not followed, so there are no loops and nothing is counted twice.

### Insights panel
- **Cleanup.** A list of folders you can probably delete, each with a safety level: *Safe*, *Regenerable*, *Review* or *Caution*.
  - **Rules** recognize known patterns such as `node_modules`, `bin/obj` next to a `.csproj`, `cmake-build-*`, `target`, browser and app caches, Windows temp, crash dumps and `Windows.old`.
  - **A local ML.NET model** finds similar folders the rules miss (details below).
- **Types.** Space by file category and by extension.
- **Age.** Space by last-modified date.
- **Duplicates.** Finds identical files in three passes: group by size, hash the first and last 64 KB, then compute a full XxHash128. Hard links are not reported as duplicates.
- **Changes.** Saves a snapshot of folder sizes after every complete scan. Compare against an earlier one to see which folders explain the growth.

### How the classifier works
There's no public dataset of folders labeled "junk" or "keep", so the model learns from your disk each time you scan:

1. The rules label the folders they're confident about. They mark both junk and keepers: source code, media, documents, `.git` history, installed apps.
2. An **ML.NET multiclass model** trains on those labels in under a second. It uses:
   - the folder's and its parent's names (words and character trigrams),
   - the mix of file types by bytes,
   - file count, average file size, age and depth,
   - whether the folder is next to a project file or inside a git repo.
3. The model then scores the folders the rules didn't decide.
4. **You can teach it.** Right-click a result and choose *Teach: this is junk* or *keep this*. Your labels count 5× in training, and the model retrains right away.

The model never suggests:

- anything smaller than 50 MB, or anything it is less than 75% sure about;
- a folder where more than 10% of the files are source code, documents or media;
- a folder inside a git repo that the repo's `.gitignore` doesn't exclude;
- anything under protected system paths.

Every model suggestion is marked *Review*. The "agreement" percentage it reports is agreement with the rule labels, not proof that it's right. Always look before you delete.

---

## Build and run

Requires the .NET 8 SDK on Windows.

```bash
dotnet run -c Release
```

To scan a folder right away, pass its path:

```bash
dotnet run -c Release -- "D:\Projects"
```

Keyboard shortcuts: **F5** rescan · **Esc** stop · **Backspace** zoom out.

## Where data is stored

Everything lives in `%AppData%\SpaceMonger\`:

| File | Contents |
|---|---|
| `settings.json` | Scan engine, color mode, panel layout |
| `labels.json` | Your junk and keep labels for the classifier |
| `snapshots\` | Folder-size history, Brotli-compressed; keeps the latest 30 per scanned folder |

## Project layout

```
Node.cs              In-memory tree (one object per file/folder)
TreemapView.cs       Treemap control: squarified layout, DIB rasterizer, color modes
Form1.cs             Main window, scanning, navigation, delete
SettingsForm.cs      Engine and display settings
Scanning/            Win32 parallel scanner, NTFS MFT reader, P/Invoke
Analysis/            File categories, breakdowns, cleanup rules, ML classifier,
                     .gitignore matcher, duplicate finder, snapshots
UI/                  Insights panel, bar chart control
```

## License

[MIT](LICENSE). Do whatever you want with it. The author clearly did.
