# Azure DevOps Pipeline Setup Guide

This guide explains how to set up the Azure DevOps pipeline for building and publishing the Siemens RF600 RFID library.

## Prerequisites

1. **Azure DevOps Organization**: Access to the Adient Azure DevOps organization
2. **Project**: Lucenec_0798_OT project
3. **Permissions**: Contributor access to the repository and Azure Artifacts feed

## Pipeline Setup

### 1. Create Azure Artifacts Feed (if not exists)

1. Navigate to **Azure Artifacts** in your Azure DevOps project
2. Click **Create Feed**
3. Name: `SiemensRFID`
4. Visibility: Choose appropriate visibility (Organization or Project)
5. Click **Create**

### 2. Configure Pipeline

1. Go to **Pipelines** ? **Create Pipeline**
2. Select **Azure Repos Git**
3. Choose the `libSiemensRFID` repository
4. Select **Existing Azure Pipelines YAML file**
5. Choose `azure-pipelines.yml` from the root
6. Click **Continue**

### 3. Configure Pipeline Variables (Optional)

You can override the default version numbers:

- `majorVersion`: Major version number (default: 1)
- `minorVersion`: Minor version number (default: 0)
- `patchVersion`: Auto-incremented patch version

To set these:
1. Click **Variables** in the pipeline editor
2. Add new variables as needed
3. Save the pipeline

### 4. Configure Service Connection (if needed)

If publishing to an external feed:
1. Go to **Project Settings** ? **Service connections**
2. Create a new **NuGet** service connection
3. Update the `publishVstsFeed` value in `azure-pipelines.yml`

## Pipeline Behavior

### Build Triggers

- **Automatic builds** on:
  - Push to `main` branch
  - Push to `develop` branch
  - Pull requests to `main` or `develop`

### Build Stages

#### Stage 1: Build and Test
1. Installs .NET 8 SDK
2. Restores NuGet packages
3. Builds the solution in Release configuration
4. Runs unit tests with code coverage
5. Packs the NuGet package
6. Publishes build artifacts

#### Stage 2: Publish (main branch only)
1. Downloads build artifacts
2. Pushes NuGet package to Azure Artifacts feed: `Lucenec_0798_OT/SiemensRFID`

## Versioning Strategy

The pipeline uses **automatic semantic versioning**:

- Format: `Major.Minor.Patch`
- Example: `1.0.15`
- The patch number auto-increments with each build

To change major or minor versions:
1. Edit pipeline variables: `majorVersion` and `minorVersion`
2. Or modify the `azure-pipelines.yml` file

## NuGet Package Configuration

### Package Information

Configured in `Adient.Automation.Lucenec.RFID.SiemensRF600.csproj`:

```xml
<PackageId>Adient.Automation.Lucenec.RFID.SiemensRF600</PackageId>
<Authors>Adient Lucenec OT</Authors>
<Description>Siemens RF600/RF680R RFID library for .NET 8</Description>
```

### Feed Configuration

The NuGet feed is configured in `NuGet.config`:

```xml
<add key="Adient-Lucenec" 
     value="https://pkgs.dev.azure.com/adient/Lucenec_0798_OT/_packaging/SiemensRFID/nuget/v3/index.json" />
```

## Using the Published Package

### 1. Configure NuGet Source

Add the Azure Artifacts feed to your project:

```bash
dotnet nuget add source https://pkgs.dev.azure.com/adient/Lucenec_0798_OT/_packaging/SiemensRFID/nuget/v3/index.json --name Adient-Lucenec
```

### 2. Authenticate

```bash
# Install Azure Artifacts Credential Provider
iex "& { $(irm https://aka.ms/install-artifacts-credprovider.ps1) }"
```

### 3. Install Package

```bash
dotnet add package Adient.Automation.Lucenec.RFID.SiemensRF600
```

Or add to your `.csproj`:

```xml
<PackageReference Include="Adient.Automation.Lucenec.RFID.SiemensRF600" Version="1.0.*" />
```

## Troubleshooting

### Build Fails on Test Stage

- Check test project references
- Ensure all test dependencies are restored
- Review test logs in Azure DevOps

### NuGet Push Fails

1. Verify feed permissions:
   - Go to **Artifacts** ? **Feed Settings** ? **Permissions**
   - Ensure build service has **Contributor** role

2. Check feed name matches:
   - Pipeline: `Lucenec_0798_OT/SiemensRFID`
   - Should match your actual feed path

### Version Conflicts

If you get version conflict errors:
- The same version already exists in the feed
- Increment the major or minor version manually
- Or delete the conflicting version from Azure Artifacts

## Manual Build

To manually trigger a build:

1. Go to **Pipelines**
2. Select the pipeline
3. Click **Run pipeline**
4. Choose the branch
5. Click **Run**

## Environment Approval

The `Publish` stage uses an environment called `Production`. To configure approvals:

1. Go to **Pipelines** ? **Environments**
2. Select **Production** (will be created on first run)
3. Click **Approvals and checks**
4. Add required approvers

## Additional Resources

- [Azure Pipelines Documentation](https://docs.microsoft.com/azure/devops/pipelines/)
- [Azure Artifacts Documentation](https://docs.microsoft.com/azure/devops/artifacts/)
- [.NET Core CLI Documentation](https://docs.microsoft.com/dotnet/core/tools/)

## Support

For issues or questions, contact the Adient Lucenec OT team.
