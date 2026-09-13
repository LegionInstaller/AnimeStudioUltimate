# ZZZ animations

Upstream AnimeStudio can't read most Zenless Zone Zero character animations. They come out empty or the export fails. This fork reads them.

## What works

- Body animations.
- Facial animations (blinking, mouth shapes, eyebrows). They are exported as shape key animation in the FBX.

Tested on the PC version: all 890 animation clips from 26 character files came out with real movement. Facial values were compared against uncompressed copies of the same clips that the game also ships, and they match.

## Tips

- Load the character's model files and animation files together. The export needs the character's Avatar, and if it's in a file you didn't load you get "can't find Avatar to deoptimize".
- The more animation files you load, the longer an FBX export with animations takes. See [performance](performance.md).

## Known issues

- One facial clip, `NPC_Female_Cecilia_Ani_Galgame_Facial_Angry`, logs "Invalid bit rate: 32". It still exports, but its face values haven't been checked.
- Shape key animation was checked in the FBX file, not after importing into Blender.

## For developers

The decoder is in `AnimeStudio.ACL/AnimeStudio.ACL.ZZZ`. It's ACL 2.1.0 with HoYoverse's patch. Facial tracks are decoded in `acl/decompression/impl/decompression.hoyo.h`.

Upstream has a newer `AnimeStudio.ACL.ZZZV2` that skips facial tracks. Don't copy it over this one.
