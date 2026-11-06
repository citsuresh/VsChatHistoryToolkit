# Publishing the toolkit

When the user asks to publish this toolkit, run the repository script `scripts\agent\Publish-VsChatHistoryToolkit.ps1` rather than manually building or copying outputs.

From the repository root, use:

```powershell
& '.\scripts\agent\Publish-VsChatHistoryToolkit.ps1' -RepositoryPath (Get-Location).Path -SharedPath 'C:\Shared'
```

Pass both paths explicitly because the script's defaults refer to the original checkout and `C:\Shared`. Honor any source or destination path explicitly requested by the user instead.

The script publishes a self-contained Windows x64 Release build into the timestamp-free `C:\Shared\VsChatHistoryToolkit-win-x64` subfolder, copies the usage documentation, and verifies the executable. Report the output path only after the script succeeds. Do not copy local chat history into the package.
