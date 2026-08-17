# nif.xml

`nif.xml` is the NIF file format description used to drive the reader and writer in
`Nif/`. It is vendored verbatim from [NifSkope](https://github.com/niftools/nifskope)
(`build/nif.xml`), which is distributed under the BSD license of the
NIF File Format Library and Tools project.

Current version: **0.9.1.0**

The file is embedded into the assembly as a resource (see `se-cmd.csproj`), so no
external data file has to ship alongside the executable.

To update, copy a newer `build/nif.xml` from a NifSkope checkout over this file and
re-run the round-trip tests.
