# dracodec_unity

draco's unity decoder plugin, used to decode `KHR_draco_mesh_compression` in google 3d tiles.

- source: https://github.com/google/draco @ `1.5.7` (8786740086a9f4d83f44aa83badfbea4dce7a1b5)
- license: apache-2.0 (see `LICENSE`)
- built on windows x64 with msvc:

```sh
cmake -S draco -B b -A x64 -DDRACO_UNITY_PLUGIN=ON -DBUILD_SHARED_LIBS=OFF \
  -DCMAKE_POLICY_DEFAULT_CMP0091=NEW -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded
cmake --build b --config Release --target dracodec_unity
```

static crt so it only depends on kernel32.
keep the build dir path short; draco's include paths overflow `MAX_PATH` otherwise.
deployed to `BONELAB/UserLibs` by `Streety.csproj`.
