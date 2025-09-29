## Existing issues
- [x] Fix mob heads being tiny, we might just need to remove padding for gui items?
- [x] Align carpets/trapdoors/pressure plates to bottom of slot like in game
- [ ] Conduit in GUI doesn't render at all
- [ ] Heavy core is a black cube with a purple top texture
- [ ] Spore blossom render has its fans flipped upside down
- [ ] Banners in the gui are facing the wrong way, we see the back of them
- [x] Compass/recovery compass/clock items should use the _00 suffix texture if they don't have one
- [ ] Backfaces on trapdoors and leaves and other blocks maybe need to be culled.
  - The current backface culling isn't working for this, it only applies to billboarded textures and breaks big_dripleaf

## Missing features
- [ ] Add way to load in potions and correctly color the bottles/splash bottles for the specific potion
- [ ] Add way to specify leather armor dye colors (also the dyeable wolf armor)
- [ ] Add armor trims to armor items
    - Use the correct color palettes and correctly darken trims that are the same material as the base (eg: iron trim + iron armor)
- [ ] Rendered inventory models need to be shaded as they are in minecraft

## Big missing features
- [ ] Add support for loading texture packs from a directory. Texture pack order can then be specified when using the renderer to use them.