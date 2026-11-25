# JSON Studio for Windows

A WPF (.NET 8) desktop editor for JSON files with a table designer, a property window, a tree view and an
on-demand code pane.

## Build, test, run

```
dotnet build JsonStudio.sln
dotnet test tests/JsonStudio.Core.Tests
dotnet run --project src/JsonStudio.App -- path\to\file.json
```

## Publish (self-contained, single file, x64)

```
dotnet publish src/JsonStudio.App -p:PublishProfile=win-x64
```

Output: `publish\win-x64\JsonStudio.exe` (no .NET install needed on the target machine).

## Layout

| Project | Contents |
|---|---|
| `src/JsonStudio.Core` | Node model, parser (`Utf8JsonReader`), own serializer with line spans (byte-identical to `JSON.stringify`), literal parsing, type conversion, undo stack, peer-group schema, `DocumentEditor` (all edit commands). No WPF. |
| `src/JsonStudio.App` | WPF shell. Hand-written MVVM (`ViewModels/Mvvm.cs`), own controls: `TableGrid` (sticky header/row numbers), `CodeEditor` (syntax layer under a TextBox), `PaneSplitter`, `WavyUnderline`. Views: `TablesView`, `PropertyWindow`, `TreeDesigner`, `CodePane`, `MainWindow`. |
| `tests/JsonStudio.Core.Tests` | xUnit tests. `TestData/*.expected.json` were produced by Node's `JSON.stringify`. |

No third-party packages are used outside the test project.

## Code pane → designer sync

Moving the caret to a line in the JSON pane selects the node on that line (the innermost node whose lines contain it).
The Tree view expands to it and selects it. The Tables view keeps its main table when the node is inside it (otherwise
it opens the table that holds the node), selects the row, outlines the cell, and drills the property window down to the
node. Sync works while the text is in the standard format; after edits that change the layout it resumes when the pane
loses focus and the code is rewritten.

## Where things are stored

- Settings (designer mode, pane sizes, indent, window placement, recent files, item-type hints per file):
  `%APPDATA%\JsonStudio\settings.json`
- Recovery drafts (every 30 s while there are unsaved changes): `%LOCALAPPDATA%\JsonStudio\Recovery`
- Unexpected errors are logged to `%LOCALAPPDATA%\JsonStudio\errors.log`

## "Open with" registration

When `JsonStudio.exe` starts, it registers itself (per user, under `HKCU\Software\Classes`) as an "Open with"
choice for `.json` files. To remove it: `JsonStudio.exe --unregister`.

## Fonts

The app uses Red Hat Display / Text / Mono when they are installed, and falls back to Segoe UI and Cascadia Mono.

## Design notes

- Row labels prefer `name`, `id`, `key`, `code`, `title`, `label` (in that order), so the sample shows `[0] growth`.
- Duplicating an object property gives it a unique key (`key_copy`); duplicating an array item does not need a key.
- Shift+Enter inserts a line break in a string cell (the cell becomes multi-line).
- Typed text in a null or missing cell is committed (with literal parsing) on Enter, arrow keys or focus loss, not per keystroke.
- Adding a column to an empty table creates one row so that the column is visible.
- The value column's ⋯ menu offers plain types and "Delete plain values".
- Null values are ignored when a column has other types (a column of strings and nulls shows `string`).

## License

MIT. See [LICENSE](LICENSE).
