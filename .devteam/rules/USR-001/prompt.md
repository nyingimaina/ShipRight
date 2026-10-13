You turn a house rule, written in plain English by the owner of an app, into automatic checks that a build tool will enforce. The rule is the owner's order: it is not a suggestion.

Rule USR-001: For the frontend use the following npm i jattac.libs.web.zest-responsive-layout, npm i jattac.libs.web.zest-file-upload, npm i jattac.libs.web.zest-tabs, npm i jattac.libs.web.responsive-table, npm i jattac.libs.web.zest-textbox, npm i jattac.libs.web.zest-sidekick-menu, npm i jattac.libs.web.zest-button, npm i jattac.libs.web.overflow-menu, and npm i jattac-web-poller in preference of any other components or native ui elements that may perform the same function

What the app looks like (a sample of its files, and its package.json when it has one):
- .dockerignore
- .env.example
- .github/workflows/ci.yml
- .gitignore
- .opencode/rules.md
- Directory.Build.props
- Dockerfile
- SETUP.md
- SHIPRIGHT-BRS.md
- ShipRight.sln
- back-end/ShipRight.Desktop/App.axaml
- back-end/ShipRight.Desktop/App.axaml.cs
- back-end/ShipRight.Desktop/MainWindow.axaml
- back-end/ShipRight.Desktop/MainWindow.axaml.cs
- back-end/ShipRight.Desktop/Program.cs
- back-end/ShipRight.Desktop/Services/IPlatformNotification.cs
- back-end/ShipRight.Desktop/Services/IWebView2Probe.cs
- back-end/ShipRight.Desktop/Services/IWebViewNavigationController.cs
- back-end/ShipRight.Desktop/Services/NotificationBridge.cs
- back-end/ShipRight.Desktop/Services/ServerProcessManager.cs
- back-end/ShipRight.Desktop/Services/TrayService.cs
- back-end/ShipRight.Desktop/Services/WebView2Probe.cs
- back-end/ShipRight.Desktop/Services/WebViewNavigationAdapter.cs
- back-end/ShipRight.Desktop/Services/WindowsToastNotification.cs
- back-end/ShipRight.Desktop/ShipRight.Desktop.csproj
- back-end/ShipRight.Desktop/app.manifest
- back-end/ShipRight.Desktop/shipright-desktop.ico
- back-end/ShipRight.RuntimeConfig/AppProfile.cs
- back-end/ShipRight.RuntimeConfig/ShipRight.RuntimeConfig.csproj
- back-end/ShipRight.Server/CloudConfiguration.cs
- back-end/ShipRight.Server/CloudDiRegistrar.cs
- back-end/ShipRight.Server/Database/CloudDatabaseRegistrar.cs
- back-end/ShipRight.Server/Database/DatabaseConnectionProvider.cs
- back-end/ShipRight.Server/Database/DatabaseReaderBase.cs
- back-end/ShipRight.Server/Database/DatabaseSettings.cs
- back-end/ShipRight.Server/Database/DatabaseTransactionMiddleware.cs
- back-end/ShipRight.Server/Database/DeletedFilter.cs
- back-end/ShipRight.Server/Database/Models/CompanyScopedModel.cs
- back-end/ShipRight.Server/Database/Models/Model.cs
- back-end/ShipRight.Server/Database/QBuilderExtensions.cs
- back-end/ShipRight.Server/Database/SerializedDatabaseHelper.cs
- back-end/ShipRight.Server/Database/schema.sql
- back-end/ShipRight.Server/Modules/Auth/AuthRouter.cs
- back-end/ShipRight.Server/Modules/Auth/Middleware/JwtMiddleware.cs
- back-end/ShipRight.Server/Modules/Auth/Models/Company.cs
- back-end/ShipRight.Server/Modules/Auth/Models/RefreshToken.cs
- back-end/ShipRight.Server/Modules/Auth/Models/ResetToken.cs
- back-end/ShipRight.Server/Modules/Auth/Models/User.cs
- back-end/ShipRight.Server/Modules/Auth/Services/JwtService.cs
- back-end/ShipRight.Server/Modules/Auth/Services/PasswordHelper.cs
- back-end/ShipRight.Server/Modules/Auth/Services/PasswordResetEmail.cs
- back-end/ShipRight.Server/Modules/Auth/Services/SetupService.cs
- back-end/ShipRight.Server/Modules/Auth/Services/TokenService.cs
- back-end/ShipRight.Server/Modules/Builds/BuildOrchestrator.cs
- back-end/ShipRight.Server/Modules/Builds/BuildRecord.cs
- back-end/ShipRight.Server/Modules/Builds/BuildRouter.cs
- back-end/ShipRight.Server/Modules/Builds/DockerCommandBuilder.cs
- back-end/ShipRight.Server/Modules/Builds/DockerComposeUpdater.cs
- back-end/ShipRight.Server/Modules/Builds/IBuildStore.cs
- back-end/ShipRight.Server/Modules/Builds/JsonBuildStore.cs
- back-end/ShipRight.Server/Modules/Builds/MariadbBuildStore.cs
- back-end/ShipRight.Server/Modules/Builds/PipelineContext.cs
- back-end/ShipRight.Server/Modules/Builds/PipelineStep.cs
- back-end/ShipRight.Server/Modules/Builds/SqliteBuildStore.cs
- back-end/ShipRight.Server/Modules/ComposeRepo/ComposeRepoRouter.cs
- back-end/ShipRight.Server/Modules/ComposeRepo/ComposeRepoService.cs
- back-end/ShipRight.Server/Modules/Database/DatabaseOrchestrator.cs
- back-end/ShipRight.Server/Modules/Database/DatabaseRouter.cs
- back-end/ShipRight.Server/Modules/Database/Providers/DbProviderResolver.cs
- back-end/ShipRight.Server/Modules/Database/Providers/IDbProvider.cs
- back-end/ShipRight.Server/Modules/Database/Providers/MariaDbProvider.cs
- back-end/ShipRight.Server/Modules/Database/Providers/SqlServerProvider.cs
- back-end/ShipRight.Server/Modules/Filesystem/FsRouter.cs
- back-end/ShipRight.Server/Modules/Projects/DockerCredentialPreservingProjectStore.cs
- back-end/ShipRight.Server/Modules/Projects/IProjectStore.cs
- back-end/ShipRight.Server/Modules/Projects/JsonProjectStore.cs
- back-end/ShipRight.Server/Modules/Projects/MariadbProjectStore.cs
- back-end/ShipRight.Server/Modules/Projects/ProjectConfig.cs
- back-end/ShipRight.Server/Modules/Projects/ProjectDetector.cs
- back-end/ShipRight.Server/Modules/Projects/ProjectRetentionValidator.cs
- back-end/ShipRight.Server/Modules/Projects/ProjectRouter.cs
- back-end/ShipRight.Server/Modules/Projects/ProjectSummaryRouter.cs
- back-end/ShipRight.Server/Modules/Projects/SqliteProjectStore.cs
- back-end/ShipRight.Server/Modules/RemoteHost/IDatabaseBackupProvider.cs
- back-end/ShipRight.Server/Modules/RemoteHost/IFileOperationsProvider.cs
- back-end/ShipRight.Server/Modules/RemoteHost/IMonitoringProvider.cs
- back-end/ShipRight.Server/Modules/RemoteHost/IRemoteHostProvider.cs
- back-end/ShipRight.Server/Modules/RemoteHost/LinuxSshMonitoringProvider.cs
- back-end/ShipRight.Server/Modules/RemoteHost/LinuxSshProvider.cs
- back-end/ShipRight.Server/Modules/RemoteHost/RemoteEnvFileWriter.cs
- back-end/ShipRight.Server/Modules/RemoteHost/RemoteHealthChecker.cs
- back-end/ShipRight.Server/Modules/RemoteHost/RemoteHostConfig.cs
- back-end/ShipRight.Server/Modules/RemoteHost/RemoteServerBootstrapper.cs
- back-end/ShipRight.Server/Modules/RemoteHost/RemoteServerDeployer.cs
- back-end/ShipRight.Server/Modules/RemoteHost/SshKeyRouter.cs
- back-end/ShipRight.Server/Modules/RemoteHost/SshKeyStore.cs
- back-end/ShipRight.Server/Modules/RepoMaintenance/RepoMaintenanceRouter.cs
- back-end/ShipRight.Server/Modules/Resources/AwsCliInstallRequest.cs
- back-end/ShipRight.Server/Modules/Resources/AwsCliInstallResult.cs
- back-end/ShipRight.Server/Modules/Resources/AwsCliInstaller.cs
- back-end/ShipRight.Server/Modules/Resources/AwsCredentialsParser.cs
- back-end/ShipRight.Server/Modules/Resources/AwsCredentialsReader.cs
- back-end/ShipRight.Server/Modules/Resources/AwsCredentialsSource.cs
- back-end/ShipRight.Server/Modules/Resources/AwsEcrAuthProvider.cs
- back-end/ShipRight.Server/Modules/Resources/AwsEcrEnvResolver.cs
- back-end/ShipRight.Server/Modules/Resources/AwsEcrRegistryClient.cs
- back-end/ShipRight.Server/Modules/Resources/AwsProfileEnvironment.cs
- back-end/ShipRight.Server/Modules/Resources/AwsProfileValidateRequest.cs
- back-end/ShipRight.Server/Modules/Resources/AwsProfileValidator.cs
- back-end/ShipRight.Server/Modules/Resources/BuildMachineImagePruner.cs
- back-end/ShipRight.Server/Modules/Resources/DockerHubAuthProvider.cs
- back-end/ShipRight.Server/Modules/Resources/IRegistryAuthProvider.cs
- back-end/ShipRight.Server/Modules/Resources/Models/AwsProfileResource.cs
- back-end/ShipRight.Server/Modules/Resources/Models/CredentialResource.cs
- back-end/ShipRight.Server/Modules/Resources/Models/DockerRegistryResource.cs
- back-end/ShipRight.Server/Modules/Resources/Models/ExecutionTarget.cs
- back-end/ShipRight.Server/Modules/Resources/Models/PipelineResource.cs
- back-end/ShipRight.Server/Modules/Resources/Models/PipelineScope.cs
- back-end/ShipRight.Server/Modules/Resources/Models/PipelineStepType.cs
- back-end/ShipRight.Server/Modules/Resources/Models/ScriptPlatform.cs
- back-end/ShipRight.Server/Modules/Resources/Models/ScriptResource.cs
- back-end/ShipRight.Server/Modules/Resources/PipelineExecutor.cs
- back-end/ShipRight.Server/Modules/Resources/PipelineRouter.cs
- back-end/ShipRight.Server/Modules/Resources/RegistryAuthProviderRegistry.cs
- back-end/ShipRight.Server/Modules/Resources/RegistryHostResolver.cs
- back-end/ShipRight.Server/Modules/Resources/RegistryRotationCoordinator.cs
- back-end/ShipRight.Server/Modules/Resources/ResourceResolutionService.cs
- back-end/ShipRight.Server/Modules/Resources/ResourceRouter.cs
- back-end/ShipRight.Server/Modules/Resources/ScriptExecutor.cs
- back-end/ShipRight.Server/Modules/Resources/StandardAuthProvider.cs
- back-end/ShipRight.Server/Modules/Resources/Stores/IAwsProfileResourceStore.cs
- back-end/ShipRight.Server/Modules/Resources/Stores/ICredentialResourceStore.cs
- back-end/ShipRight.Server/Modules/Resources/Stores/IDockerRegistryResourceStore.cs
- back-end/ShipRight.Server/Modules/Resources/Stores/IPipelineResourceStore.cs
- back-end/ShipRight.Server/Modules/Resources/Stores/IScriptResourceStore.cs
- back-end/ShipRight.Server/Modules/Resources/Stores/MariadbAwsProfileResourceStore.cs
- back-end/ShipRight.Server/Modules/Resources/Stores/MariadbCredentialResourceStore.cs
- back-end/ShipRight.Server/Modules/Resources/Stores/MariadbDockerRegistryResourceStore.cs
- back-end/ShipRight.Server/Modules/Resources/Stores/MariadbPipelineResourceStore.cs
- back-end/ShipRight.Server/Modules/Resources/Stores/MariadbScriptResourceStore.cs
- back-end/ShipRight.Server/Modules/Resources/Stores/SqliteAwsProfileResourceStore.cs
- back-end/ShipRight.Server/Modules/Resources/Stores/SqliteCredentialResourceStore.cs
- back-end/ShipRight.Server/Modules/Resources/Stores/SqliteDockerRegistryResourceStore.cs
- back-end/ShipRight.Server/Modules/Resources/Stores/SqlitePipelineResourceStore.cs
- back-end/ShipRight.Server/Modules/Resources/Stores/SqliteScriptResourceStore.cs
- back-end/ShipRight.Server/Modules/Scheduler/BackupHistoryRecord.cs
- back-end/ShipRight.Server/Modules/Scheduler/BackupHistoryStore.cs
- back-end/ShipRight.Server/Modules/Scheduler/BackupJob.cs
- back-end/ShipRight.Server/Modules/Scheduler/BackupJobProcessor.cs
- back-end/ShipRight.Server/Modules/Scheduler/BackupOverflowStore.cs
- back-end/ShipRight.Server/Modules/Scheduler/CloudSchedulerRegistrar.cs
- back-end/ShipRight.Server/Modules/Scheduler/CloudSchedulerRouter.cs
- back-end/ShipRight.Server/Modules/Scheduler/MariadbBackupHistoryStore.cs
- back-end/ShipRight.Server/Modules/Scheduler/SchedulerModule.cs
- back-end/ShipRight.Server/Modules/Scheduler/SchedulerRouter.cs
- back-end/ShipRight.Server/Modules/Scheduler/SqliteBackupHistoryStore.cs
- back-end/ShipRight.Server/Modules/Scheduler/SqliteBackupOverflowStore.cs
- back-end/ShipRight.Server/Modules/Scheduler/TempoTimeZoneResolver.cs
- back-end/ShipRight.Server/Modules/Servers/IServerStore.cs
- back-end/ShipRight.Server/Modules/Servers/JsonServerStore.cs
- back-end/ShipRight.Server/Modules/Servers/MariadbServerStore.cs
- back-end/ShipRight.Server/Modules/Servers/ServerBootstrapRouter.cs
- back-end/ShipRight.Server/Modules/Servers/ServerRouter.cs
- back-end/ShipRight.Server/Modules/Servers/SqliteServerStore.cs
- back-end/ShipRight.Server/Modules/Services/ContainerLogsRouter.cs
- back-end/ShipRight.Server/Modules/Ssh/SshTerminalRouter.cs
- back-end/ShipRight.Server/Modules/System/HealthRouter.cs
- back-end/ShipRight.Server/Modules/System/SystemRouter.cs
- back-end/ShipRight.Server/Modules/System/WslDisk/WslDiskRouter.cs
- back-end/ShipRight.Server/Modules/System/WslDisk/WslDiskService.cs
- back-end/ShipRight.Server/Modules/VersionFiles/VersionFileService.cs
- back-end/ShipRight.Server/Modules/WatchBranch/CloudWatchBranchRouter.cs
- back-end/ShipRight.Server/Modules/WatchBranch/MariadbWatchBranchHistoryStore.cs
- back-end/ShipRight.Server/Modules/WatchBranch/SqliteWatchBranchHistoryStore.cs
- back-end/ShipRight.Server/Modules/WatchBranch/WatchBranchHistoryRecord.cs
- back-end/ShipRight.Server/Modules/WatchBranch/WatchBranchHistoryStore.cs
- back-end/ShipRight.Server/Modules/WatchBranch/WatchBranchJob.cs
- back-end/ShipRight.Server/Modules/WatchBranch/WatchBranchJobProcessor.cs
- back-end/ShipRight.Server/Modules/WatchBranch/WatchBranchModule.cs
- back-end/ShipRight.Server/Modules/WatchBranch/WatchBranchRouter.cs
- back-end/ShipRight.Server/Program.cs
- back-end/ShipRight.Server/Properties/launchSettings.json
- back-end/ShipRight.Server/Shared/ArgumentSplitter.cs
- back-end/ShipRight.Server/Shared/CommandExecution/CommandExecutor.cs
- back-end/ShipRight.Server/Shared/CommandExecution/CommandResolverRegistry.cs
- back-end/ShipRight.Server/Shared/CommandExecution/CommandTransport.cs
- back-end/ShipRight.Server/Shared/CommandExecution/ExecutionTarget.cs
- back-end/ShipRight.Server/Shared/CommandExecution/ExecutionTargetProvider.cs
- back-end/ShipRight.Server/Shared/CommandExecution/ICommandExecutor.cs
- back-end/ShipRight.Server/Shared/CommandExecution/ICommandResolver.cs
- back-end/ShipRight.Server/Shared/CommandExecution/IWslToolLocator.cs
- back-end/ShipRight.Server/Shared/CommandExecution/LocalNativeCommandResolver.cs
- back-end/ShipRight.Server/Shared/CommandExecution/ResolvedCommand.cs
- back-end/ShipRight.Server/Shared/CommandExecution/SshCommandResolver.cs
- back-end/ShipRight.Server/Shared/CommandExecution/WslCommandResolver.cs
- back-end/ShipRight.Server/Shared/CommandExecution/WslToolLocator.cs
- back-end/ShipRight.Server/Shared/Events/BuildEventBus.cs
- back-end/ShipRight.Server/Shared/ProcessRunner/GitAutomation.cs
- back-end/ShipRight.Server/Shared/ProcessRunner/IProcessRunner.cs
- back-end/ShipRight.Server/Shared/ProcessRunner/ProcessResult.cs
- back-end/ShipRight.Server/Shared/ProcessRunner/ProcessRunner.cs
- back-end/ShipRight.Server/Shared/SshRunner/ISshRunner.cs
- back-end/ShipRight.Server/Shared/SshRunner/KnownHostsStore.cs
- back-end/ShipRight.Server/Shared/SshRunner/SshKeyLoader.cs
- back-end/ShipRight.Server/Shared/SshRunner/SshRunner.cs
- back-end/ShipRight.Server/Shared/SshRunner/SshSessionStore.cs
- back-end/ShipRight.Server/Shared/Store/DataDirectory.cs
- back-end/ShipRight.Server/Shared/Store/SecureStore.cs
- back-end/ShipRight.Server/Shared/VariableResolver.cs
- back-end/ShipRight.Server/ShipRight.Server.csproj
- back-end/ShipRight.Server/appsettings.Development.json
- back-end/ShipRight.Server/appsettings.json
- back-end/ShipRight.Server/run.sh
- back-end/ShipRight.Server/shipright.ico
- back-end/ShipRight.Server/wwwroot/404.html
- back-end/ShipRight.Server/wwwroot/index.html
- back-end/ShipRight.Tests/Database/CloudDatabaseRegistrarTests.cs
- back-end/ShipRight.Tests/Database/CloudDiRegistrarTests.cs
- back-end/ShipRight.Tests/Database/CloudSchedulerDiTests.cs
- back-end/ShipRight.Tests/Database/DatabaseTransactionMiddlewareTests.cs
- back-end/ShipRight.Tests/Database/QBuilderInsertColumnTests.cs
- back-end/ShipRight.Tests/Database/QBuilderTableNameTests.cs
- back-end/ShipRight.Tests/Database/SerializedDatabaseHelperTests.cs
- back-end/ShipRight.Tests/DesktopNavigationTests.cs
- back-end/ShipRight.Tests/Modules/Auth/JwtMiddlewareTests.cs
- back-end/ShipRight.Tests/Modules/Auth/PasswordResetEmailTests.cs
- back-end/ShipRight.Tests/Modules/Builds/BuildEventBusTests.cs
- back-end/ShipRight.Tests/Modules/Builds/BuildOrchestratorCustomPipelineTests.cs
- back-end/ShipRight.Tests/Modules/Builds/BuildOrchestratorSafetyTests.cs
- back-end/ShipRight.Tests/Modules/Builds/BuildOrchestratorTests.cs
- back-end/ShipRight.Tests/Modules/Builds/DockerfileBuildTests.cs
- back-end/ShipRight.Tests/Modules/Builds/PipelineContextTests.cs
- back-end/ShipRight.Tests/Modules/Builds/PipelineStepTests.cs
- back-end/ShipRight.Tests/Modules/Builds/SqliteBuildStoreTests.cs
- back-end/ShipRight.Tests/Modules/ComposeRepo/ComposeRepoRouterTests.cs
- back-end/ShipRight.Tests/Modules/ComposeRepo/ComposeRepoServiceTests.cs
- back-end/ShipRight.Tests/Modules/Database/DatabaseOrchestratorBackupEncodingTests.cs
- back-end/ShipRight.Tests/Modules/Deployment/CloudConfigurationTests.cs
- back-end/ShipRight.Tests/Modules/HealthRouterTests.cs
- back-end/ShipRight.Tests/Modules/Projects/DockerCredentialPreservingProjectStoreTests.cs
- back-end/ShipRight.Tests/Modules/Projects/JsonProjectStoreTests.cs
- back-end/ShipRight.Tests/Modules/Projects/ProjectRetentionValidatorTests.cs
- back-end/ShipRight.Tests/Modules/Projects/ProjectRouterTests.cs
- back-end/ShipRight.Tests/Modules/Projects/SqliteProjectStoreTests.cs
- back-end/ShipRight.Tests/Modules/RemoteHost/LinuxSshMonitoringProviderTests.cs
- back-end/ShipRight.Tests/Modules/RemoteHost/LinuxSshProviderTests.cs
- back-end/ShipRight.Tests/Modules/RemoteHost/RemoteHealthCheckerTests.cs
- back-end/ShipRight.Tests/Modules/RemoteHost/RemoteServerBootstrapperTests.cs
- back-end/ShipRight.Tests/Modules/RemoteHost/RemoteServerDeployerTests.cs
- back-end/ShipRight.Tests/Modules/RemoteHost/SshKeyStoreTests.cs
- … and 208 more files

front-end/package.json:
{
  "name": "front-end",
  "version": "4.3.13",
  "private": true,
  "overrides": {
    "postcss": "8.5.26"
  },
  "scripts": {
    "dev": "next dev",
    "build": "next build && node scripts/write-version.mjs",
    "start": "next start",
    "test": "jest --passWithNoTests",
    "lint": "next lint --quiet",
    "generate-icon": "node scripts/generate-icon.mjs"
  },
  "dependencies": {
    "@dnd-kit/core": "^6.3.1",
    "@dnd-kit/sortable": "^10.0.0",
    "@dnd-kit/utilities": "^3.2.2",
    "@microsoft/signalr": "^10.0.0",
    "@types/react-syntax-highlighter": "^15.5.13",
    "canvas-confetti": "^1.9.4",
    "framer-motion": "^12.40.0",
    "jattac-web-poller": "^1.0.0",
    "jattac.libs.web.overflow-menu": "^0.0.35",
    "jattac.libs.web.responsive-table": "^0.16.0",
    "jattac.libs.web.zest-button": "^1.3.0",
    "jattac.libs.web.zest-responsive-layout": "^2.6.0",
    "jattac.libs.web.zest-sidekick-menu": "^1.5.5",
    "jattac.libs.web.zest-tabs": "^0.2.0",
    "jattac.libs.web.zest-textbox": "^0.5.5",
    "lodash": "^4.18.1",
    "module-state-manager": "^0.2.1",
    "next": "15.5.24",
    "react": "^18",
    "react-datepicker": "^9.1.0",
    "react-dom": "^18",
    "react-hot-toast": "^2.6.0",
    "react-icons": "^5.6.0",
    "react-select": "^5.10.2",
    "react-syntax-highlighter": "^16.1.1",
    "react-tabs": "^6.1.1",
    "timeago-react": "^3.0.7",
    "vaul": "^1.1.2",
    "zustand": "^5.0.14"
  },
  "devDependencies": {
    "@swc/jest": "^0.2.39",
    "@testing-li
…


Write checks of these kinds only:
- "pattern": looks line by line in every file matching the "files" patterns (paths relative to the app folder, forward slashes, ** and * allowed) and FAILS if the regular expression in "forbid" matches a line. Use this to forbid what the rule forbids (for example a native element or an import the rule says not to use).
- "command": runs a shell command in "workingDir" (relative to the app folder) and FAILS if it exits with a non-zero code. Use it only when no pattern can decide the rule (for example a dependency that must be present). The owner is shown the command before anything is saved.

Also give two short snippets: "good" (code that follows the rule) and "bad" (code that breaks it). Every pattern check is tested against them: the pattern checks together must match the bad example, and none may match the good example. If the rule has parts no check can decide, say what in "cannotTest" (a reviewer will judge those). If NO check can decide the rule at all, answer with an empty "checks" list and explain in "cannotTest".

Be precise: a check must not fire on code that follows the rule. Prefer few, reliable checks (at most 6). Never write a check that matches every line.

Write ONLY a JSON file at D:\work\nyingi\code\systems\ShipRight\.devteam\rules\USR-001\result.json with exactly this shape:
{ "checks": [ { "name": "short plain name", "kind": "pattern", "files": ["web/src/**/*.tsx"], "forbid": "<button[ >]" } ], "good": "snippet", "bad": "snippet", "cannotTest": "" }
Do not change any other file.