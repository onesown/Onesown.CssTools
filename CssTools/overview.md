# CssTools for Visual Studio

**CssTools** adds rich IntelliSense support for CSS Custom Properties (CSS variables) and CSS class names directly inside Visual Studio 2022/2026 — in `.css`, `.razor`, `.html`, and `.cs` files.

---

## Features

### 🎯 Hover Tooltips for CSS Variables
Hover over any `--my-variable` reference and instantly see **all definitions** across your entire solution:
- Grouped by **project name**
- Shows **value** and **file location**
- Every entry is a **clickable link** → jumps directly to the definition

### 🎨 Hover Tooltips for CSS Class Names
Hover over a CSS class name in `class="..."` attributes (HTML, Razor) or in `.css` files:
- Lists **all matching CSS definitions** across the solution
- Grouped by project, with file and line number as clickable links
- Works inside Razor expressions like `@(condition ? "class-a" : "class-b")`

### 💡 CSS Variable IntelliSense (Autocomplete)
Type `--` anywhere in a `.css`, `.razor`, `.html`, or `.cs` file to get a full IntelliSense list of all known CSS variables:
- Shows **all scanned variables** sorted alphabetically
- Each entry displays the **first known value** as a suffix
- Selecting an item inserts the full **`var(--name)`** expression — ready to use
- Description tooltip shows all definitions grouped by project (same style as hover tooltips)
- Replaces the default CSS completer while `--` is active, then restores it automatically

### ⚡ Always Up-to-Date
- **Startup scan** indexes all `.css` files in your solution automatically
- **File system watcher** keeps the index live — changes outside the editor are picked up instantly

### ⚙️ Flexible Configuration (`.csstools.json`)
Place a `.csstools.json` next to your `.sln` to control which files are scanned:

```json
{
  "exclude": [ "**/themes/**", "**/PlaywrightTests/**" ],
  "include": [ "**/themes/light.css" ]
}
```

`include` always wins over `exclude`. Config changes are picked up without restarting Visual Studio.

### 📋 Show CSS Variables Command
**Tools → Show CSS Variables** dumps all known variables with their values and locations to the Output window — useful for auditing your design tokens.

---

## Supported File Types

| File type | CSS Variables | CSS Classes | Autocomplete (`--`) |
|---|:---:|:---:|:---:|
| `.css` | ✅ | ✅ | ✅ |
| `.razor` / `.blazor` | ✅ | ✅ | ✅ |
| `.html` | ✅ | ✅ | ✅ |
| `.cs` | ✅ | — | ✅ |

---

## Requirements

- Visual Studio 2022 **17.14** or newer (including Visual Studio 2026)
- .NET Framework 4.5 or newer

