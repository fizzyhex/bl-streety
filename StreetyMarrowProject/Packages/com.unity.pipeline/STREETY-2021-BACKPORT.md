# Streety Unity 2021 compatibility

Embedded copy of fizzyhex/unity-pipeline-backport at commit
4e38bc9c409b2ed6b6ced095a3ac44ea83f0647b, adapted for Unity 2021.3.16f1.

Changes beyond the upstream 2022 backport:

- Use Object.FindObjectsOfType(..., true) before Unity 2022.2.
- Read BuildReport.files before Unity 2022.1.
- Use EditorUserBuildSettings.il2CppCodeGeneration before Unity 2022.1.
- Return null for unavailable main-thread frame timing before Unity 2022.1.
- Set the package minimum Unity version to 2021.3.

The package compiled successfully in an isolated Unity 2021.3.16f1 project.
Individual build, profiling, and code reload commands are not comprehensively tested.

Keep this embedded package. `unity pipeline install` or an upstream upgrade
would replace these compatibility changes.
