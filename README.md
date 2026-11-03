# Modstoke Virginia

Optional appearance and companionship features for Virginia. Everything is off by default.

Appearance settings are local to each player. They cover garment colors, camouflage fabric detail, texture filtering, mesh detail distance, skinning quality and animation culling. Optional paired assets add 4K head and body skin, a blue floral dress variation, a refined clothed silhouette, subtle idle motion and restrained secondary physics. Native limbs, facial shapes, animation clips, collision and AI remain in place.

Relationship gameplay requires the host's permission and each participant's own opt-in. Greetings, appreciation and quiet companionship can be enabled separately. Bond is progression owned by this extension, not a modification of Virginia's native sentiment. Interactions do not force her movement, affection, gifts or responses.

## Source Layout

Start at [Plugin.cs](Plugin.cs) for native mod entry points.

| Folder | Contents |
| --- | --- |
| [Runtime](Runtime) | Actor access, lifecycle and network messages. |
| [Appearance](Appearance) | Materials, skin, model assets and secondary motion. |
| [Relationships](Relationships) | Interactions, progression and persistence. |
| [UI](UI) | Menu and preference registration. |

The [project file](Modstoke.Virginia.csproj) lists sources explicitly. Keep
related partials together and preserve the existing entry order when moving
files; partial field initializers can depend on that order.

## Settings

Open **Mods > Virginia** for status and interactions. The Virginia settings provider has separate **Local appearance**, **Personal relationship** and **Host gameplay** groups.

Enable appearance customization, then choose the individual visual options. Outfit tint applies only to recognized Virginia garments. Fabric detail requires the camouflage suit. Higher-detail rendering and texture copies can increase rendering cost and graphics memory use.

**4K blue floral dress** applies only to the original blue dress. It retains the native alpha mask, normal map and material effects. Other dress colors remain unchanged.

**4K skin detail** adds fine surface detail while preserving the original colors, facial features, alpha and texture-island borders. Only head and body base-color maps change. Native normals, wrinkles, dirt, blood and wetness maps remain in use; the native wrinkle controller continues to update its material. This is a restrained texture refinement, not a replacement of her identity or a complete character remaster.

**Refined clothed silhouette** adjusts the body and original leotard together by up to about 12 mm. **Breathing and relaxed idle motion** adds small breathing and occasional shoulder-settling shapes. It fades out outside neutral idle, during transitions and when fear, item handling or unsafe actor states are detected. It does not trigger gestures, change controller parameters or replace native animation clips. These options require the original body and leotard and share the 25-meter viewing limit.

**Subtle chest and hip motion** uses four added controls and the game's existing DynamicBone solver. It requires the matching body, leotard and completed native bone remap. Motion pauses beyond 25 meters, resets on large position changes and releases its owned mesh changes on scene loads, costume changes or disabling the option. The default strength is restrained; this staged implementation still needs in-game tuning and compatibility acceptance. Other outfits have no authored secondary-motion weights yet.

The optional assets must match the extension build and sit beside its DLL in `assets/`: `virginia-presentation.json`, `virginia-head-4k.png`, `virginia-body-4k.png` and `virginia-dress-blue.png`. Missing or mismatched assets leave the corresponding native visuals intact. These private, game-derived assets are not a redistributable public asset pack.

The staged build has authoring previews and compilation checks, not in-game acceptance. Skin integration, costume changes, native animation layering, secondary-motion feel and multiplayer compatibility still need a playthrough. The isolated authoring preview omits game-owned hair and eyeballs.

For relationships, the host enables romance gameplay and the desired interactions. Each participating player enables **Participate in romance**. Approach Virginia before requesting an interaction. Quiet companionship requires remaining nearby and safe for the full interaction; leaving, death, fear, combat or a changed player/actor lifetime interrupts it. Quiet companionship is currently available only to the local single-player or host player. Remote clients can use greetings and appreciation; remote quiet time is unavailable until the SDK can verify the required health state.

## Multiplayer And Saves

The host and participating players need this extension for its relationship menu and messages. Other players can remain unmodded. Installing it does not require enabling ownership migration or replacing the Client tablet mod.

The host validates requests and owns relationship progress. Local appearance choices are never sent to other players. Progress follows the current world save; single-player progress uses the loader's save-sidecar support. Disabling a feature preserves existing progress. **Step back from this relationship** resets only your extension progress.

Use a backup of your save for the first playthrough with any new mod.
