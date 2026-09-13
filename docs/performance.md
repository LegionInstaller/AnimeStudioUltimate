# Performance

Loading and exporting ZZZ files is faster than upstream. The exported files are the same as before.

| What | Before | After |
|---|---|---|
| Loading 3.4 GB of game files | 37.1 s | 11.1 s |
| Loading 690 MB | 6.9 s | 1.6 s |
| CLI export, character files | 42.1 s | 23.5 s |
| CLI export, mixed files | 62.4 s | 22.1 s |
| FBX export with 20 heavy animations | 122 s | 2.8 s |

Measured on a 16-thread machine with 32 GB RAM.

## Why an export with animations can still be slow

It depends on how many animation files you load, not on the character. Load only the model and the export takes seconds. Load a character's whole moveset and it can take minutes.

Unticking "Export animations" alone doesn't make it faster, because the animations are still read, just not written. Untick "Collect animations" too, or don't load the animation files.

## More speed with lots of RAM

Setting the environment variable `DOTNET_gcServer=1` makes exports a lot faster (23.5 s down to 15.2 s on the character files). It also uses more memory while loading, and on 32 GB it can make loading slower. Only worth it with plenty of RAM.

## What changed

Files are loaded and parsed in parallel, a lot of unneeded copying was removed, the CLI exports several files at once, and a slow lookup in the animation conversion was replaced. The GUI export still runs one file at a time.
