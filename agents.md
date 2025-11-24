# Agents Guide

This repo is collaborative. Whenever you implement or change a feature, follow these rules:

1. **Always update the Wiki**  
   - Any new UI control, rendering change, or install/deployment tweak must be reflected in `wiki/` (Rendering Pipeline, Configuration & Controls, Build & Install, etc.).
   - If your change doesn’t fit an existing page, add a new one and link it from `wiki/Home.md`.

2. **Keep the README current**  
   - Summarize big features, scripts, or workflows so new contributors see the latest state immediately.

3. **Scripts + Docs stay in sync**  
   - If you add or change a script (like `deploy.ps1`), document how/when to run it in both `README.md` and `wiki/Build-and-Install.md`.

4. **Context Menu or UX changes**  
   - Update `wiki/Configuration-and-Controls.md` whenever the context menu gains or loses options.

5. **Rendering/engine updates**  
   - Update `wiki/Rendering-Pipeline.md` to explain how the new logic works (color mapping, input sources, performance considerations, etc.).

When in doubt, assume **every functional change needs accompanying documentation**. Keeping the wiki truthful is part of the definition of done.
