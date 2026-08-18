# Test resources

Sample assets copied from [ck-cmd](https://github.com/aerisarn/ck-cmd)'s
`examples/blender` directory. Each `.nif` has an `.fbx` counterpart exported from
the same Blender scene, which makes them useful for both round-trip tests and,
later, conversion tests.

All the NIFs are Skyrim-era **20.2.0.7** files.

| File | Contents |
| --- | --- |
| `generate_rb` | A rigid body with no shape geometry |
| `generate_rb_box` | A rigid body with a box collision shape |
| `generate_rb_sphere` | A rigid body with a sphere collision shape |
| `multi_material_cube` | A cube carrying more than one material |
| `generate_rb_box_with_mesh` | FBX only: box rigid body plus render mesh |
| `generate_rb_box_with_transform_mesh` | FBX only: as above, with a transformed mesh |

These are treated as opaque fixtures: tests assert that loading and re-saving a
file reproduces it byte for byte, so the files themselves must not be re-saved or
normalised.
