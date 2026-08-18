# nifly test corpus

NIF files from [nifly](https://github.com/ousnius/nifly), the library behind
BodySlide and Outfit Studio, taken from its `tests/input` directory at commit
`134124c`.

nifly is **GPL-3.0**, the same licence as se-cmd, so these are redistributed
under that licence. Copyright remains with the nifly authors.

## Why these are here

The fixtures in the parent directory are all Skyrim LE files produced by a single
exporter, which is a narrow slice of what a NIF can be. These cover considerably
more:

| File | What it exercises |
| --- | --- |
| `TestNifFile_Skinned_SE.nif` | Skinned Skyrim SE, `BSTriShape` geometry |
| `TestNifFile_Skinned_Dynamic_SE.nif` | Skinned and dynamic |
| `TestNifFile_Skinned_NoNiSkinDataWeights.nif` | A skin whose weights live only in the partition |
| `TestNifFile_Optimize_Dynamic_LE_to_SE.nif` | Skinned LE, `NiTriShape` geometry |
| `TestNifFile_Optimize_Dynamic_SE_to_LE.nif` | Skinned SE |
| `TestNifFile_Optimize_LE_to_SE.nif`, `_SE_to_LE.nif` | Unskinned LE and SE pairs |
| `TestNifFile_DeepGraph_SE.nif` | 185 blocks, deep block graph |
| `TestNifFile_LooseBlocks_SE.nif` | Blocks nothing references, plus a real bone hierarchy |
| `TestNifFile_MultiBound_SE.nif` | Multi-bound nodes |
| `TestNifFile_OrderedNode_SE.nif` | `BSOrderedNode` |
| `TestNifFile_Furniture_Col_SE.nif` | Furniture markers and collision |
| `TestNifFile_Animated_LE.nif` | Controllers and interpolators |
| `TestNifFile_RootNonZero.nif` | A root block that is not block 0 |
| `TestNifFile_FixBSXFlags_*.nif` | `BSXFlags` variants |
| `TestNifFile_FixShaderFlags_*.nif` | Shader flag variants |
| `TestNifFile_Static_SE.nif` | Plain static SE mesh |
| `TestNifFile_Corrupted.nif` | Deliberately corrupt; loading it **must** fail |

They are treated as opaque: the tests assert that loading and re-saving
reproduces each file byte for byte, so they must not be re-saved or normalised.

## Not included

Files for games this project does not target were left out: Morrowind, Oblivion,
Fallout 4, Fallout 76 and Starfield. The Fallout 76 and Starfield files also do
not load, the latter because `BSGeometry` does not exist in nif.xml 0.9.1.0.
