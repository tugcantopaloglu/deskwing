---
name: DeskWing
description: A local workspace for stream controls, shared ink, Windows pen input, and a secondary display.
colors:
  canvas: "#101318"
  surface: "#1c222b"
  surface-high: "#252d38"
  outline: "#354151"
  ink: "#f5f7fa"
  secondary: "#b8c3d1"
  cyan: "#78dce8"
  violet: "#b7a0f9"
  amber: "#ffd58a"
  red: "#ff8e93"
  blue: "#9ac9f5"
  green: "#7fe0a1"
  primary-ink: "#101e22"
  field: "#131922"
  danger-surface: "#39242b"
  danger-outline: "#67404c"
  paper: "#f6f3e9"
  pen-graphite: "#1b2530"
  pen-blue: "#295a95"
  pen-berry: "#8a355b"
  pen-green: "#36704f"
  pen-ochre: "#a85a16"
typography:
  body:
    fontFamily: "Manrope, system-ui, sans-serif"
    fontSize: "16px"
    fontWeight: 400
    letterSpacing: "normal"
  display:
    fontFamily: "Manrope, system-ui, sans-serif"
    fontSize: "clamp(22px, 2.6vw, 38px)"
    fontWeight: 800
    letterSpacing: "-.035em"
  headline:
    fontFamily: "Manrope, system-ui, sans-serif"
    fontSize: "clamp(24px, 2.8vw, 34px)"
    fontWeight: 700
    letterSpacing: "-.025em"
  title:
    fontFamily: "Manrope, system-ui, sans-serif"
    fontSize: "22px"
    fontWeight: 700
  key-label:
    fontFamily: "Manrope, system-ui, sans-serif"
    fontSize: "clamp(16px, 1.9vw, 29px)"
    fontWeight: 800
    lineHeight: 1.2
  label:
    fontFamily: "Manrope, system-ui, sans-serif"
    fontSize: "13px"
    fontWeight: 700
  help:
    fontFamily: "Manrope, system-ui, sans-serif"
    fontSize: "13px"
    fontWeight: 400
    lineHeight: 1.6
  navigation:
    fontFamily: "Manrope, system-ui, sans-serif"
    fontSize: "13px"
    fontWeight: 700
  display-choice:
    fontFamily: "Manrope, system-ui, sans-serif"
    fontSize: "14px"
    fontWeight: 700
    lineHeight: 1.6
rounded:
  badge: "6px"
  navigation: "8px"
  field: "10px"
  control: "12px"
  channel: "14px"
  key: "16px"
  dialog: "18px"
spacing:
  tight: "4px"
  compact: "8px"
  control-gap: "10px"
  small: "12px"
  medium: "16px"
  section: "20px"
  large: "24px"
  editor-columns: "28px"
  content: "32px"
components:
  button-primary:
    backgroundColor: "{colors.cyan}"
    textColor: "{colors.primary-ink}"
    rounded: "{rounded.control}"
    padding: "10px 18px"
  button-quiet:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.ink}"
    rounded: "{rounded.control}"
    padding: "10px 18px"
  button-quiet-hover:
    backgroundColor: "{colors.surface-high}"
    textColor: "{colors.ink}"
    rounded: "{rounded.control}"
  button-danger:
    backgroundColor: "{colors.danger-surface}"
    textColor: "{colors.red}"
    rounded: "{rounded.control}"
    padding: "10px 18px"
  input:
    backgroundColor: "{colors.field}"
    textColor: "{colors.ink}"
    rounded: "{rounded.field}"
    padding: "11px 13px"
  navigation-current:
    backgroundColor: "{colors.surface-high}"
    textColor: "{colors.cyan}"
    typography: "{typography.navigation}"
    rounded: "{rounded.navigation}"
    padding: "9px 12px"
  studio-key:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.blue}"
    rounded: "{rounded.key}"
    padding: "12px 8px"
  editor-key:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.violet}"
    rounded: "{rounded.control}"
    padding: "23px 9px 13px"
  status-badge:
    backgroundColor: "{colors.surface-high}"
    textColor: "{colors.secondary}"
    rounded: "{rounded.badge}"
    padding: "5px 9px"
  pen-paper:
    backgroundColor: "{colors.paper}"
    rounded: "{rounded.control}"
  pen-input-pad:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.secondary}"
    rounded: "{rounded.control}"
  pen-sync-conflict:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.secondary}"
    rounded: "{rounded.field}"
    padding: "14px 16px"
  ink-tool-drawer:
    backgroundColor: "{colors.canvas}"
    textColor: "{colors.ink}"
    rounded: "{rounded.control}"
    padding: "16px"
  pen-input-tools:
    backgroundColor: "{colors.canvas}"
    textColor: "{colors.ink}"
    rounded: "{rounded.control}"
    padding: "16px"
  ink-window-tool-drawer:
    backgroundColor: "{colors.canvas}"
    textColor: "{colors.ink}"
    rounded: "{rounded.control}"
    padding: "14px"
  pc-preview-status:
    textColor: "{colors.secondary}"
    typography: "{typography.help}"
  pc-preview-error:
    textColor: "{colors.amber}"
    typography: "{typography.help}"
  display-rate-select:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.ink}"
    rounded: "{rounded.control}"
    typography: "{typography.display-choice}"
    padding: "8px 12px"
  display-start:
    backgroundColor: "{colors.cyan}"
    textColor: "{colors.primary-ink}"
    rounded: "{rounded.control}"
    typography: "{typography.label}"
    padding: "10px 18px"
  display-compatibility:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.ink}"
    rounded: "{rounded.control}"
    typography: "{typography.label}"
    padding: "10px 18px"
---

# Design System: DeskWing

## Overview

**Creative North Star: "A working surface beside your PC"**

DeskWing retains the established studio interface: a near-black canvas, slate controls, bundled Manrope, authored line icons, and cyan emphasis. The direction is an extension of the working product. Large, legible controls and stable status positions support touch operation while the PC carries configuration and review.

The interface includes Stream Studio, Computer Copilot, shared Ink & notes, Pen tablet, and Second display. Each task gets a useful working area. The board editor shows the board beside its inspector; shared ink gets a quiet paper canvas; Windows pen input gets a clearly labeled target and session state; secondary Windows content gets an unobstructed view. Connection, local save, sharing, and active input state remain explicit.

**Key Characteristics:**

- Dark, compact studio surfaces with clear boundaries.
- One bundled type family and a small functional color vocabulary.
- Large tablet controls, visible focus, and labels beside state colors.
- Task-specific working areas with recoverable editing and readable status.
- Paper appears only as the surface for user-created ink.

This document refreshes the earlier provisional direction for DeskWing 1.14. The frontmatter is the normative extracted token set. `.impeccable/design.json` includes a synchronized, machine-readable source snapshot and component examples. The implementation remains the evidence for responsive overrides; this is not a claim that every screen uses every token.

## Colors

The palette uses cool slate neutrals and light, readable accents. Preserve the exact incumbent CSS values; avoid substituting approximate blues or grays during new work.

### Primary

- **Cyan** marks the main action, keyboard focus, selected workspace, active state, and save action.
- **Primary ink** is the dark text used on cyan controls.

### Secondary

- **Violet** identifies scene-oriented keys.
- **Amber** identifies sound-oriented keys and recovery attention.
- **Blue** identifies application-oriented keys.
- **Red** identifies broadcast and destructive controls.
- **Green** supports connected and successful state.

### Neutral

- **Canvas** is the page and panel background.
- **Surface** is the resting control and sidebar fill; **surface-high** is the hover/current layer.
- **Outline** separates sections and bounds controls without a heavy grid.
- **Ink** is primary text; **secondary** is supporting copy and labels.
- **Field** is the inset form fill. **Danger surface** and **danger outline** distinguish a destructive action without filling the page with red.
- **Paper** belongs to the scratchpad. Graphite, blue, berry, green, and ochre are user-selectable drawing colors, independent of studio status semantics.

**The State Has Words Rule.** Pair connection, warning, selected, and error colors with a label, icon, outline, or explicit pressed/current state. A colored dot alone does not explain a state.

## Typography

**Display, body, and controls:** bundled variable Manrope, then `system-ui`, then `sans-serif`. The local font file is `companion/wwwroot/fonts/Manrope.ttf`, declared for weights 200–800. Do not add a remote font dependency.

The interface uses the same family for headings and controls. Heavier labels make repeated keys easy to locate; supporting copy stays quieter. No decorative display face or invented monospace role is needed.

### Hierarchy

- **Display** is the tablet header brand treatment, with a 24px override between 761px and 1100px. It is hidden in full-pad mode and is not a general heading style.
- **Headline** is the Ink & notes and Pen tablet heading. The desktop overview uses a 28px heading and the console brand uses 25px.
- **Title** is the desktop header at its main size. It steps to 19px below 1000px and 18px below 620px.
- **Key label** is the large tablet action text. Responsive and full-pad overrides reduce it to preserve usable keys.
- **Label** is form metadata. **Help** is supporting copy; longer instructions generally stay within 65–72 characters per line.
- **Navigation** is compact desktop task navigation. Group titles use 12px, weight 600, and 1.5 line height.

Use tabular numerals for dB readings, counts, width values, and planned macro delays. Long labels may wrap; do not let them force horizontal page scrolling.

## Layout

The tablet begins with a status/header row, a working area, and bottom page navigation. Studio uses a key grid with a side mixer; full pad supports 24 or 40 visible keys per group. The base grid is four columns and three rows. Existing media rules adjust density and header/navigation sizes for narrow or short screens.

The desktop console has a 216px sidebar and 72px header. At 1000px or below the sidebar becomes 184px. At 620px or below navigation moves above the workspace and scrolls horizontally. **Create**, **Operate**, **Connections & setup**, and **Settings & recovery** are task groups; the latter two can collapse. Administrative forms occupy the content area rather than floating over an unrelated dashboard.

The board editor uses a preview/inspector split: `minmax(320px, 1.3fr) minmax(280px, 1fr)`, with a 28px gap. At 1100px or below this becomes `minmax(240px, 1fr) minmax(230px, .9fr)` with an 18px gap. At 760px or below the inspector follows the preview vertically. Preview density choices are 12, 24, and 40; the preview paginates instead of growing without bounds.

Ink uses a 3:1 drawing/notes split with a 24px gap. The paper has an 8:5 aspect ratio. At 1050px or below notes move beneath the drawing. At 580px or below toolbar spacing and paper corners tighten. Immersive fullscreen has zero outer padding or reserved toolbar space: the paper fills the background, with floating tools/exit and an optional overlay drawer. The actual drawing preserves its ratio; unused paper-colored bands on other screen ratios are not drawable. Show/Hide notes changes available space without stretching the drawing. At 700px or below fullscreen notes move under the drawing.

Pen tablet also removes permanent chrome in fullscreen. Its default Fill tablet mapping uses the whole input area and can stretch coordinates relative to a differently proportioned target. Match PC proportions fits the target ratio with inactive edges. Keep that user-visible distinction explicit.

Both native PC windows use resizable/maximizable borders and a 620×480 minimum window size. Main DeskWing defaults to 1280×860; DeskWing Ink defaults to 1000×740. The separate ink view keeps the ordinary Windows caption and border, removes the application sidebar/header, and fits the paper into the full client area with zero outer padding. Floating tools and optional notes overlays preserve drawing space at smaller window sizes. Window sizing is independent of the shared document and is not persisted across newly created windows.

Second display setup keeps resolution and refresh selectors beside their labels, with a wrapping 12px gap and 48px minimum control height. Start and compatibility-view buttons use 44px minimum height and 13px labels. The native player fits the source aspect ratio within a black video surface; its exit and telemetry stay visible in native overlays. These native controls use platform fonts, documented separately from the bundled web typography.

Primary tablet and ordinary form controls use a 48px minimum height. New editor, pen, and live-filter controls commonly use 44px. Desktop sidebar controls use 42px at normal widths and 44px in the narrow layout; compact editor selection/drag affordances are smaller supporting targets. Preserve these distinctions instead of claiming every target is 44px.

Spacing tokens record recurring observed values, not an enforced mathematical scale. Keep aligned edges and reserve the largest gaps for task boundaries. Test 1280×720 and 900×620 desktop layouts, 1280×800 and 800×600 tablet layouts, and the 390px narrow fallback.

## Elevation & Depth

Depth primarily comes from surface tone, a single-pixel outline, and spacing. Editor columns and readiness rows use dividers, not nested decorative cards. Transient notices, the fader thumb, the floating handwriting shortcut, and immersive controls/tool drawers use small shadows to distinguish them from the working surface. A modal backdrop darkens the workspace; ordinary desktop task views are full-area panels with no radius or shadow.

The key press uses a brief 120ms ease-out color/transform transition and a `.965` scale. Editor key hover/outline transitions use the same duration. Reduced-motion preferences remove transitions and smooth scrolling. Loading, saving, empty, disconnected, and conflict states must remain readable without animation.

**The Working Area Rule.** Do not add elevation, illustration, or card framing that consumes the board, inspector, drawing, or display area without helping the current task.

## Shapes

Controls use gently rounded corners. Reuse the extracted radii: small badges, compact navigation, inset fields, regular controls, mixer channels, large keys, and dialogs each have an existing size. The scratchpad uses the regular control radius, reduced to the navigation radius on very narrow screens. Round status dots express state; pill styling is reserved for existing active tags.

Icons use the project's authored SVG path vocabulary. Standard line icons have no fill, round caps and joins, and a 1.8 stroke width; large studio keys use 1.65. Copy the existing icon rather than substituting an unrelated icon family. User-selected key images and drawings are content.

## Components

### Buttons and fields

Quiet buttons are slate with an outline; primary buttons are cyan with dark ink; danger buttons use a dark red fill and red text. Keep action labels in sentence case. Fields use the inset fill, a visible outline, 15px text, and the existing 48px form height. Field placeholders use secondary text at full opacity. Base button, link, and field focus selectors use a 3px cyan outline at a 3px offset; the workspace supplies a 2px fallback for other focusable elements. The canvas uses its own inset blue focus outline.

### Grouped navigation

The active desktop item uses cyan text, a raised slate fill, and `aria-current="page"`. Workspaces also remain reachable through the compact mode selector. Keep setup credentials in their connection task and keep **Board editor**, **Ink & notes**, **Pen tablet**, and **Second display** directly findable.

### Board preview and inspector

Preview keys look like their operational counterparts but select content for editing. They never execute actions. A selected key has a cyan outline and a **Selected** label; checkboxes enable multiple selection and dedicated drag handles support ordering. Selection controls remain visually distinct from the action content. Batch style and transfer tools replace the individual inspector when several keys are selected.

Undo/redo sit above the preview. A single save bar reports saved, unsaved, saving, or storage failure. Recovery and conflict banners appear before the editor; they explain whether a draft is local and what a replacement would affect. Live-source keys are read-only previews, with guidance when the source has not been saved or is unavailable.

### Typed macro steps

Steps use numbered fieldsets, type-specific labels, inline validation, and Earlier/Later controls alongside drag. Planned delays are visually separated from execution time. Discovery and **Check step** are read-only actions. Nested groups use a single left divider and an explicit summary rather than repeated heavy cards.

### Live operation and recovery

Soundpad's **All**, **Favourites**, and **Recent** choices stay beside search and category controls. Copilot's **Follow app** and **Pin layout** use explicit pressed state and a nearby sentence describing the current behavior. Readiness rows show **Fix**, **Review**, or **Checked**, then the relevant action and next step. Backup recovery leads with restoring one inactive profile; the full-replacement comparison is under a disclosure.

### Ink & notes

Keep Pen/Eraser, ink color, width, Undo/Redo/Clear, and **Ignore fingers on canvas** together above the paper in the normal view and together in the immersive tools drawer. The palm preference is persistent and labeled. The selected tool uses cyan and `aria-pressed`. Notes remain an ordinary labeled text area beside or beneath the canvas; handwriting is an entry method for that field. Local backup status and PC-sharing status are different messages. Explicit conflict controls explain both versions before replacing either one; in fullscreen they live in the tools drawer.

Fullscreen keeps only floating **Show tools** and **Exit fullscreen** controls visible by default, each at least 44px. Offline/conflict/save failures add an amber attention mark and an accessible attention label to Show tools. The drawer opens the ink controls, notes/export actions, local/sync state, and conflict recovery over the drawing. Export remains available there on narrow screens. Escape closes the drawer before leaving fullscreen. Keep the fixed paper ratio and preserve committed data when a partial stroke is cancelled.

### Dedicated PC ink window

DeskWing Ink opens directly onto the fitted paper, with floating **Show tools** and **Fullscreen** controls and an attention mark when sharing or saving needs review. This compact presentation does not automatically enter browser or native fullscreen. Show tools opens the drawing, export, sharing, and conflict controls in an overlay; **Show notes** opens a separate notes overlay with **Hide notes**. Drawing on the canvas or pressing Escape closes the overlays. Keep the ordinary window caption available for moving and resizing. The paper retains its 8:5 ratio, with unused paper-colored bands when the window proportions differ.

### Pen tablet

Visually distinguish the live Windows input surface from the shared paper document. Show the selected display/application, readiness, and whether a pen session is active. Start/Stop and the fullscreen exit must remain discoverable. Pressure and other telemetry describe input samples; they are not evidence that a specific target app painted a stroke. A change of focus, geometry, connection, or lifecycle must visibly end the current session when the input contract requires it.

The normal input pad uses the regular slate surface within an inset field-colored stage, fits the chosen target's aspect ratio, and adds a cyan border for an active connection. During an active pen session, the selected target's JPEG preview sits behind the input surface and follows the same geometry as its pen coordinates. A small outlined cyan cursor becomes filled during contact. Keep loading, unavailable, and stopped states explicit, and clear obsolete frames. Preview pixels are application content, not sample artwork or evidence that every reported input sample produced a mark.

The preview image fills the pad with `object-fit: fill` and does not receive pointer events. The target/status area shows **Loading the live PC view…**, **Live PC view**, or an unavailable explanation. Preview errors use amber text and open the tools overlay in fullscreen; input can remain active when capture fails. PC enable/disable labels name both **pen & live view** so the shared pixels are part of the stated action.

Immersive fullscreen removes stage padding, borders, corner radius, heading, and footer. Floating **Show tools**, **Start pen**/**Stop pen**, and **Exit fullscreen** remain at least 44px. Show tools reveals target/status/mapping details in a bounded overlay; Back to controls remains in the normal view. Fill tablet is the immersive default; Match PC proportions is an explicit alternative. Entering/leaving fullscreen or changing mapping ends the old session before using new coordinates.

### Second display

Give the shared desktop area room and keep its lifecycle/sharing state legible. **Start second display** is the primary action for the native H.264 viewer; **Open compatibility preview** is a quiet, explicit alternative for bounded JPEG viewing at up to 4 fps. Do not silently substitute the preview after a failed native start. **Refresh rate** offers **60 Hz** and **Match tablet**, with current and maximum tablet refresh reported separately. On the PC, **Windows display refresh rate** configures the source monitor; it is not a delivery counter.

Keep selected target Hz, source Hz, device Hz, and measured received/shown fps distinct in wording. Android reports decoder-rendered **fps shown** and **fps received**; iOS reports received fps and screen cadence, not displayed-video fps. **Exit display** must stay reachable. Native overlays use black video framing and platform controls; the web setup retains the incumbent Manrope, slate surfaces, cyan primary action, and visible focus. Neither display path supplies audio or forwards touch/pen input. Pen tablet remains a separate workspace with its own preview and input authorization.

## Do's and Don'ts

### Do:

- Do reuse the exact base palette, bundled Manrope, and existing authored icons.
- Do keep task controls near the content they affect.
- Do show explicit saved, selected, disabled, disconnected, and recovery states.
- Do preserve keyboard focus, readable labels, reduced motion, and narrow layouts.
- Do treat drawings, key images, and shared desktop pixels as user content.

### Don't:

- Don't introduce a second visual identity for editor, ink, or recovery screens.
- Don't use color as the only explanation of state.
- Don't make editor preview keys perform live actions.
- Don't hide save failures, draft conflicts, or source availability behind a toast alone.
- Don't decorate the canvas with sample ink that could be mistaken for a saved drawing.
- Don't visually promise full palm rejection, built-in handwriting recognition, universal drawing-app support, or a guaranteed delivered display frame rate.
