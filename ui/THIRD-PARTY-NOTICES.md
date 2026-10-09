# Third-party notices

## dot-matrix-dashboard-ui

The visual design and the dot renderers in `ui/DotUi.cs` (dot bar, dot ring, 5x7 dot-matrix glyphs,
sparkline, palette tokens, card/pill/switch styling) are ported from the **dot-matrix-dashboard-ui**
skill by **karminski**:

- Source: https://github.com/karminski/karminski-design-skills/tree/main/dot-matrix-dashboard-ui
- License: Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International (CC BY-NC-SA 4.0),
  https://creativecommons.org/licenses/by-nc-sa/4.0/

Changes: WPF `OnRender` geometry ported to GDI+; a light palette added for the light glass material;
components adapted to an owner-drawn WinForms widget and launch console.

Because of this, the `ui/` presentation layer, and distributions that include it, fall under
CC BY-NC-SA 4.0: non-commercial use only, credit the original author, and share adaptations under
the same license.
