# Quick Start: Push to Azure DevOps

## Files Created

The following files have been created in your repository root:

1. **azure-pipelines.yml** - Azure DevOps build and publish pipeline
2. **NuGet.config** - NuGet package source configuration
3. **AZURE-PIPELINE-SETUP.md** - Detailed setup instructions

## Next Steps

### 1. Review the Pipeline Configuration

Open `azure-pipelines.yml` and verify:
- Feed name: `Lucenec_0798_OT/SiemensRFID` matches your Azure Artifacts feed
- Branch triggers are correct
- Version numbers are appropriate

### 2. Commit and Push to Azure DevOps

```bash
# Navigate to repository root
cd "C:\Users\akrnanv\OneDrive - Adient\Documents\GitHub\libSiemensRFID"

# Check current status
git status

# Add new files
git add azure-pipelines.yml
git add NuGet.config
git add AZURE-PIPELINE-SETUP.md
git add QUICKSTART.md

# Commit changes
git commit -m "Add Azure DevOps pipeline for build and publish"

# Push to Azure DevOps (choose your remote)
git push azure main
# OR
git push origin main
```

### 3. Set Up Pipeline in Azure DevOps

1. Go to: https://adient.visualstudio.com/Lucenec_0798_OT/_build
2. Click **New pipeline**
3. Select **Azure Repos Git**
4. Choose **libSiemensRFID** repository
5. Select **Existing Azure Pipelines YAML file**
6. Choose `/azure-pipelines.yml`
7. Click **Run**

### 4. Create Azure Artifacts Feed (if needed)

If the feed `SiemensRFID` doesn't exist:

1. Go to: https://adient.visualstudio.com/Lucenec_0798_OT/_artifacts
2. Click **Create Feed**
3. Name: `SiemensRFID`
4. Visibility: **Organization** or **Project** scope
5. Click **Create**

### 5. Configure Feed Permissions

1. Go to your feed: https://adient.visualstudio.com/Lucenec_0798_OT/_artifacts/feed/SiemensRFID
2. Click **Feed Settings** (gear icon)
3. Click **Permissions**
4. Add build service: **Project Collection Build Service (adient)**
5. Grant **Contributor** role

## Pipeline Outputs

Once the pipeline runs successfully:

### Main Branch
- ? Build solution
- ? Run tests
- ? Pack NuGet package
- ? Publish to Azure Artifacts feed

### Other Branches (develop, PRs)
- ? Build solution
- ? Run tests
- ? Pack NuGet package
- ? Does NOT publish (only validates)

## Package Version

The pipeline automatically versions your package:
- **Format**: `Major.Minor.Patch`
- **Example**: `1.0.0`, `1.0.1`, `1.0.2`...
- **Patch** auto-increments with each build

## Using the Package

After successful publish, install in other projects:

```bash
# Add the feed (one-time setup)
dotnet nuget add source https://pkgs.dev.azure.com/adient/Lucenec_0798_OT/_packaging/SiemensRFID/nuget/v3/index.json --name Adient-SiemensRFID

# Install the package
dotnet add package Adient.Automation.Lucenec.RFID.SiemensRF600
```

## Troubleshooting

### Authentication Issues
Install Azure Artifacts Credential Provider:
```powershell
iex "& { $(irm https://aka.ms/install-artifacts-credprovider.ps1) }"
```

### Feed Not Found
Verify the feed URL in the error matches:
```
https://pkgs.dev.azure.com/adient/Lucenec_0798_OT/_packaging/SiemensRFID/nuget/v3/index.json
```

### Permission Denied
Ensure the build service has **Contributor** role in the feed permissions.

## Support

For detailed information, see `AZURE-PIPELINE-SETUP.md`.
