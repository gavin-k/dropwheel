---
name: dropwheel-tool
description: Build a DropWheel tool from a one-line request. Use when the user wants a new item on their DropWheel wheel (轮盘), a custom file action or conversion for DropWheel, or to rearrange / group DropWheel's Tools or Actions wheel. Writes tool.json + a script into %LOCALAPPDATA%\DropWheel\tools and validates it with DropWheel.exe --validate-tool.
---

# DropWheel tool

DropWheel is a Windows app: drag a file, tap a modifier key, a 6-sector wheel appears, drop on a sector. Three wheels: Convert (Shift), **Tools** (Ctrl+Alt) and **Actions** (Win). A user tool is a folder with a `tool.json`: either a **recipe** (built-in items run in turn, no code) or a **script** (pwsh / python / exe). DropWheel watches the tools folder and loads new tools while it runs. Nothing here needs network access, and DropWheel never calls a model; **you** are the AI.

## Workflow

1. **Pin down three things** before writing anything: what the tool does to one file, which wheel it belongs on (`tools` for transformations that produce a file, `actions` for things like moving, tagging, uploading), and which kinds of file it applies to. Ask only when the request leaves one of these open.
2. **Recipe first.** If the job is a chain of what DropWheel already does (convert, compress, resize, rotate, strip metadata, merge PDFs, zip), write a recipe (see *Recipes*): no code, nothing to enable, undo and naming as for the built-ins. Write a script only for what the built-ins cannot do.
3. **Write the folder** `%LOCALAPPDATA%\DropWheel\tools\<slug>\` with `tool.json` and the script. One tool per folder, no nesting, no leading dot. Use the slug from the id (`user.strip-exif` → folder `strip-exif`). If DropWheel runs with `--data-dir <dir>`, the folder is `<dir>\tools\<slug>\` instead.
4. **Validate**: run `DropWheel.exe --validate-tool "<folder>"` (see *Finding DropWheel.exe*). It prints JSON with `problems`; exit code 0 means clean, 1 means at least one error, 2 means usage. Fix every entry whose `isWarning` is not `true`, then run it again. A warning about a missing command means the interpreter is not on PATH: tell the user, or write the absolute path into `command`.
5. **Tell the user** where the tool is. A recipe is usable at once. A script tool is on the wheel but greyed out: script tools run only after the user opens **Settings › 我的工具 (My tools)**, reads the command line shown there and turns on **允许运行 (Allow to run)**. Changing `command` or `args` later turns it off again until they re-enable it. Do not try to work around this.
6. Only if asked, edit `layout.json` to move or group items (see *Layout*).

Prefer the built-in wheel items when one already does the job (compress image, resize, merge/split PDF, OCR, compress video, extract audio, unzip; copy path, move to, rename, zip), a recipe when several of them in a row do it, and a script only for what DropWheel cannot do itself.

## Recipes

```json
{
  "$schema": "https://raw.githubusercontent.com/gavin-k/dropwheel/main/docs/tool-manifest.schema.json",
  "schema": 1,
  "id": "user.web-jpg",
  "name": { "zh": "发网上", "en": "Web JPG" },
  "hint": { "zh": "缩到一半、转成 JPG、压到 70", "en": "Half size, JPG, quality 70" },
  "wheel": "tools",
  "appliesTo": ["image"],
  "kind": "recipe",
  "steps": [
    { "op": "tool.half-size" },
    { "convert": "jpg" },
    { "op": "tool.compress-image", "options": { "quality": 70 } }
  ]
}
```

`steps` runs in order; each step's outputs are the next step's inputs. A step is either `{ "op": "<built-in id>", "options": { ... } }` or `{ "convert": "<extension>" }` (a target of the Convert wheel, such as `jpg`, `png`, `webp`, `pdf`, `mp3`; it takes no options). Steps run per file; `tool.combine-pdf`, `tool.merge-pdf` and `action.zip` take all files at once and produce one. A step that writes nothing for a file (compress found it small enough already) passes the file on unchanged. Only the last step writes where the user's outputs go, so naming, duplicate handling, undo and cancel are the last step's; earlier outputs live in a hidden temporary folder that is removed afterwards. Each step's suffix stays in the name: the recipe above writes `photo-600x400-压缩.jpg`.

Steps are built-ins only: no `user.*` ids, no scripts, no recipes inside recipes. Set `appliesTo` whenever the first step is `convert` (otherwise it is taken from the first step). `undo: "none"` turns undo off for the last step's outputs.

Built-ins that take `options`:

| Step | Option | Values |
|---|---|---|
| `tool.compress-image` | `quality` | 1 to 100; JPEG and WebP only, never above the source's own quality. Default 80. |
| `tool.half-size` | `percent` | 1 to 100, of the side length. Default 50. |
| `tool.rotate-right` | `degrees` | `90`, `180` or `270` clockwise. Default 90. |

Every other built-in takes none; `--validate-tool` rejects unknown options and out-of-range values. Items that ask the user something (`action.move-to`, `action.copy-to`, `action.rename`), only show something (`action.copy-path`) or delete (`action.recycle`) are not steps. `tool.extract` and `tool.split-pdf` write a folder, which no later step reads, so they can only come last. `--validate-tool` checks the first convert step's format and every `op`; whether a convert step's engine exists for the files actually dropped is found out when it runs.

## tool.json

```json
{
  "$schema": "https://raw.githubusercontent.com/gavin-k/dropwheel/main/docs/tool-manifest.schema.json",
  "schema": 1,
  "id": "user.strip-exif",
  "name": { "zh": "去掉 EXIF", "en": "Strip EXIF" },
  "hint": { "zh": "复制一份，不带拍摄信息", "en": "A copy without camera metadata" },
  "wheel": "tools",
  "appliesTo": ["image"],
  "kind": "script",
  "script": {
    "command": "pwsh",
    "args": ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "{toolDir}\\strip-exif.ps1", "{input}", "{output}"],
    "perFile": true,
    "output": { "mode": "new-file", "extension": "same", "suffix": "-clean" },
    "timeoutSeconds": 600
  },
  "placement": { "group": { "zh": "我的", "en": "Mine" }, "index": 5 },
  "undo": "recycle"
}
```

| Field | Required | Rules |
|---|---|---|
| `schema` | yes | Always `1`. |
| `id` | yes | `^user\.[a-z0-9-]+$`. Must be unique across the tools folder; the folder name is unrelated. |
| `name` | yes | String, or `{ "zh", "en" }`. DropWheel shows `en` only when its UI language is English; `zh` is the fallback, so always give `zh` (or a plain string). |
| `hint` | no | Same shape; one short line shown in the wheel's centre while hovering. |
| `icon` | no | A [Segoe Fluent Icons](https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font) code point, written as `E7A7`, `U+E7A7` or `\uE7A7`, drawn above the name on the sector. Pick one from the font's list, such as `E8C8` (Copy), `E7AD` (Rotate), `E8FE` (Scan), `EA90` (PDF), `E898` (Upload). Anything else is a warning and the sector shows no icon. |
| `wheel` | yes | `tools` or `actions`. |
| `appliesTo` | no | Array of `image`, `video`, `audio`, `pdf`, `document` (DOCX), `slides` (PPTX), `sheet` (XLSX/CSV), `subtitle`, `archive`. Omit or `null` = every kind, including unknown files and folders. |
| `kind` | no | `script` (default) or `recipe`. A recipe has `steps` and no `script`; a script has `script` and no `steps`. |
| `steps` | recipe | See *Recipes*. |
| `script.command` | yes | Absolute path, a path relative to the tool folder, or a bare name looked up in the tool folder and then PATH (with PATHEXT, so `python` finds `python.exe`). `pwsh` falls back to Windows PowerShell 5.1 with a warning (see *Encoding*). |
| `script.args` | yes | Array of strings. Each argument is passed as is, no shell, no quoting needed, even with spaces. Placeholders below. |
| `script.perFile` | no | Default `true`: the script runs once per dropped item with `{input}`. `false`: it runs once with all items, and must use `{inputs}`. |
| `script.output.mode` | no | `new-file` (default): the script writes exactly one file to `{output}`. `none`: the script writes nothing DropWheel should track (it may still move or upload the input). |
| `script.output.extension` | no | `same` (default) keeps the source's extension; otherwise letters and digits, no dot (`txt`, `webp`). |
| `script.output.suffix` | no | Appended to the source's stem, such as `-clean`. Default empty. |
| `script.timeoutSeconds` | no | 1 to 86400, default 600. |
| `placement` | no | `group` (string or `{zh, en}`) and/or `index` 0 to 5. A hint used **once**, when the tool is first seen; after that the user's `layout.json` rules. |
| `undo` | no | `recycle` (default): undo moves the outputs to the Recycle Bin. `none`: the job cannot be undone; use it whenever the script changes or deletes the input. |

Placeholders in `args`: `{input}` (one source path), `{inputs}` (an argument equal to exactly `{inputs}` expands to one argument per source; inside a longer string they are joined by spaces), `{output}` (the file to write, only with `mode: new-file`), `{outputDir}` (the folder the output will land in), `{toolDir}` (the tool's folder, also the working directory).

Environment variables set for the script: `DROPWHEEL_INPUT` (first source), `DROPWHEEL_INPUTS` (all sources, newline separated), `DROPWHEEL_OUTPUT` and `DROPWHEEL_TEMP` (only with `mode: new-file`), `DROPWHEEL_OUTPUT_DIR`, `DROPWHEEL_TOOL_DIR`, `PYTHONIOENCODING=utf-8`, `PYTHONUTF8=1`.

## Script protocol

- **Write the output to `{output}` and nowhere else.** The path is inside a temporary folder next to the destination, with the final extension already applied. DropWheel moves it into place afterwards, applies the user's naming template and duplicate policy (number, skip, replace, ask), and records it for undo. A script that writes next to the input on its own bypasses all of that.
- With `perFile: false` the single output is named after the first source.
- Exit code `0` is success. Anything else fails the job; the last lines of stderr become the error the user sees, so print a short reason there.
- Optional stdout lines, one per line, UTF-8:
  - `progress 0.42` (0 to 1) drives the progress bar.
  - `detail some text` shows under the job.
  - `skip reason` ends this item as skipped, no output expected.
- Over `timeoutSeconds` the whole process tree is killed and the job fails. Cancelling a job kills it the same way. Closing DropWheel kills running tools.
- stdin is not connected; never prompt. No GUI, no `Read-Host`, no `input()`.
- The process runs below normal priority, with the tool folder as working directory.

### Encoding

- **pwsh / PowerShell scripts: the first statement (right after `param`, if any) must be** `[Console]::OutputEncoding = [Text.Encoding]::UTF8`, or the stdout protocol and any non-ASCII stderr arrive garbled. Do not name a parameter `$Input`; it is a PowerShell automatic variable.
- Python: DropWheel sets `PYTHONIOENCODING=utf-8` and `PYTHONUTF8=1`; nothing to do.
- Other executables: print UTF-8.

## Finding DropWheel.exe

Try in order, and use the first that exists:

1. `dropwheel.exe` on PATH (the MSIX / Store install registers this alias).
2. `%ProgramFiles%\DropWheel\DropWheel.exe` (the MSI install).
3. The `InstallLocation` value of the uninstall key whose `DisplayName` is `DropWheel`, under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*` or the `HKCU` equivalent.

Both switches work while DropWheel is running and do not disturb it. `--list-tools` prints every tool folder with its problems; `--validate-tool <folder>` checks one folder, including whether its id collides with a sibling.

## Trust and privacy

A script tool is another process on the user's machine, started with the user's rights. DropWheel cannot see what it reads or whether it goes online; DropWheel's own network counter in Settings does not include it. That is why every script tool starts disabled and the user must enable it once after reading the command. Keep scripts short, keep them in the tool folder, and say in `hint` what the tool touches. Never download or install anything from inside a tool script; if a dependency is missing, say so on stderr and exit 1.

## Layout

`%LOCALAPPDATA%\DropWheel\layout.json` holds the arrangement. A running DropWheel reloads it within a second of a change and applies its own later writes on top of what is on disk, so editing it while DropWheel runs is safe; write the whole file in one go. Edit it only when the user asks to move, group, or hide items. Shape:

```json
{
  "version": 1,
  "wheels": {
    "tools":   { "image": [ { "item": "tool.compress-image" }, null, { "group": { "name": { "zh": "我的", "en": "Mine" }, "items": ["user.strip-exif", "user.watermark"] } }, ... ] },
    "actions": { "any":   [ { "item": "action.copy-path" }, ... ] }
  },
  "installed": ["user.strip-exif"]
}
```

- `wheels.tools` has one list per file kind (`image`, `video`, `audio`, `pdf`, `document`, `slides`, `sheet`, `subtitle`, `archive`, `unknown`); `wheels.actions` has only `any`.
- Each list has up to 6 slots, clockwise from the top; `null` is an empty sector. A slot is `{ "item": "<id>" }` or `{ "group": { "name", "items": [ids] } }`, at most 6 items, groups cannot nest. A group opens as an outer ring when the pointer rests on it.
- A kind without a list keeps DropWheel's built-in order; a kind with a list shows exactly what the list says, so include the built-ins you want to keep. Built-in ids: `tool.compress-image`, `tool.half-size`, `tool.rotate-right`, `tool.strip-metadata`, `tool.combine-pdf`, `tool.merge-pdf`, `tool.split-pdf`, `tool.ocr`, `tool.compress-video`, `tool.extract-audio`, `tool.mute`, `tool.extract`; `action.copy-path`, `action.copy-to`, `action.move-to`, `action.rename`, `action.zip`, `action.recycle`. Each applies only to some kinds; an id that does not apply to the kind is ignored. Settings › 我的工具 lists anything a laid-out wheel leaves off.
- `installed` records which user tools already had their `placement` hint applied. Leave it alone.

## Examples

Three working tools live beside this file under `examples/`: `recipe-web-jpg` (a recipe: half size, JPG, quality 70), `hello-pwsh` (a script that copies an image with a `-copy` suffix and reports progress) and `hello-python` (a script that writes a `-info.txt` next to any file). Copy one, rename the id, folder and script, keep the UTF-8 line of the PowerShell script.

## Checklist before you say "done"

- `--validate-tool` exit code 0, no non-warning problems.
- `id` is unique, `name.zh` present.
- A recipe wherever the built-ins can do the job; a script only otherwise.
- Script: output goes to `{output}` only; nothing else is created beside the input.
- `undo: "none"` if the input is modified or deleted.
- PowerShell: first statement sets UTF-8 output.
- Told the user on which wheel (Ctrl+Alt or Win) it appears, and for a script tool, to enable it in Settings › 我的工具.
