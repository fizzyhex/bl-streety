## Style

streety prefers a concise, casual, lower-case style for user-facing text; with the only exception being location names, and other bodies of text where readability may be hindered.

streety likes to conform to bonelab's ui styles and sfx while extending creativity where necessarily: uses bonelab ui assets and sfx, but is never afraid to extend past it if it holds streety back.

## Co-Authoring Policy

Agents must only co-sign commits following this schema:

```
Co-signed by virtual <cat species> 🐾 / <pseudo-email>
```

Example: Co-signed by virtual calico 🐾 / kindcalico@donotreply.com

## Multiplayer Native

Features that concern the rig and changing of world state should make considerations for compatibilites with Bonelab Fusion:

https://github.com/Lakatrazz/BONELAB-Fusion

Level events can be synchronised using RPC Variables / RPC Events. Features should account for the possibility of multiple rigs owned by other players being able to co-exist.

Maintain compatibility with singleplayer for users who aren't using Fusion, or don't have it installed.

## README

The README.md is user-facing, and should avoid inducing information overload through implementation rundowns, and things better seen in the source code. Avoid adding to it unless asked, or if information is invalidated by changes.

## Tonality in Code Comments and Summaries

Keep code comments and documentation grounded, natural, and technically precise. Prefer describing what the code does, why it exists, or what constraints it enforces over narrating its behaviour through metaphors, personification, or dramatic phrasing. Avoid overly stylised, aphoristic, or authoritative language that sounds more like storytelling than engineering documentation. A conversational tone is fine, but comments should read like something a thoughtful developer would write for another developer. Don't overcorrect into rigid formality, and avoid comments that merely restate the code.