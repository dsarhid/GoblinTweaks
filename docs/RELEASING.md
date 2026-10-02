# Releasing

1. Bump `<Version>` in `GoblinTweaks/GoblinTweaks.csproj` (e.g. 1.0.0 → 1.1.0) and commit.
2. Tag and push:
   ```powershell
   git tag v1.1.0
   git push origin main v1.1.0
   ```
3. GitHub Actions builds, publishes a release with `latest.zip` and updates `repo.json`.
   Everyone using the custom repository URL gets the update from `/xlplugins`.

First time only: in the GitHub repository, *Settings → Actions → General → Workflow permissions*
→ **Read and write**, so the workflow can commit `repo.json`.
