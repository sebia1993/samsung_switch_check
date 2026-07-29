namespace SamsungSwitchWatch.Agent.Setup.Deployment;

public sealed class SetupDiagnosticsService(
    IAgentPackageValidator packageValidator,
    ISetupFileSystem fileSystem,
    IServiceManager serviceManager,
    IFirewallManager firewallManager,
    IAgentHealthProbe healthProbe,
    IAdministratorChecker administratorChecker,
    DeploymentPaths paths)
{
    public async Task<SetupOperationResult> RunAsync(
        SetupRequest request,
        CancellationToken cancellationToken)
    {
        var steps = new List<SetupStepResult>();
        try
        {
            if (!administratorChecker.IsAdministrator())
            {
                throw new SetupException(
                    SetupErrorCodes.AdministratorRequired,
                    "Agent 서비스 설치에는 관리자 권한이 필요합니다.");
            }

            steps.Add(Success("ADMINISTRATOR_OK", "권한 확인", "관리자 권한으로 실행 중입니다."));

            var journalStore = new DeploymentJournalStore(fileSystem, paths);
            if (journalStore.Exists)
            {
                steps.Add(new SetupStepResult(
                    "RECOVERY_PENDING",
                    "이전 작업 복구",
                    SetupStepState.Information,
                    "완료되지 않은 이전 설치가 있습니다. 설치/업데이트를 누르면 먼저 자동 복구합니다."));
                return SetupOperationResult.Success(
                    "이전 설치 복구가 필요합니다.",
                    steps);
            }

            ValidateInput(request);
            steps.Add(Success(
                "INPUT_VALID",
                "입력 확인",
                "Viewer IP와 관리망 선택·추가가 올바릅니다."));

            var package = packageValidator.Validate(paths.PackageDirectory);
            steps.Add(Success(
                "PACKAGE_VALID",
                "패키지 확인",
                $"Agent {package.Version} 파일 무결성이 정상입니다."));

            var service = serviceManager.Capture(SetupConstants.ServiceName);
            fileSystem.ValidateDeploymentPaths(paths, service, []);
            if (!fileSystem.CanCreateUnder(paths.InstallDirectory) ||
                !fileSystem.CanCreateUnder(paths.DataDirectory))
            {
                throw new SetupException(
                    SetupErrorCodes.PathNotWritable,
                    "Program Files 또는 ProgramData 설치 경로의 상위 폴더를 확인할 수 없습니다.");
            }

            steps.Add(Success(
                "PATHS_READY",
                "경로 사전 확인",
                "설치·데이터 경로 형식과 상위 폴더를 확인했습니다. 실제 쓰기 권한과 EDR 허용 여부는 설치 중 확인합니다."));
            var firewallAssessment = firewallManager.AssertSecurityGate(
                SetupConstants.HttpsPort,
                paths.AgentExecutablePath);
            AddFirewallWarnings(steps, firewallAssessment);
            steps.Add(Success(
                "FIREWALL_GATE_READY",
                "방화벽 보안",
                firewallAssessment.Warnings.Count == 0
                    ? "Windows 방화벽 기본 차단과 Viewer 전용 규칙 적용 조건이 정상입니다."
                    : "필수 방화벽 보안 조건은 정상입니다. 다른 허용 규칙은 유지하고 Agent에서 Viewer IP를 추가로 제한합니다."));

            steps.Add(new SetupStepResult(
                service.Exists ? "SERVICE_FOUND" : "SERVICE_NOT_INSTALLED",
                "서비스 상태",
                SetupStepState.Information,
                service.Exists
                    ? service.Running ? "기존 Agent 서비스가 실행 중입니다." : "기존 Agent 서비스가 중지되어 있습니다."
                    : "신규 설치 대상입니다."));

            var firewall = firewallManager.Capture(SetupConstants.FirewallRuleName);
            var exactFirewall = FirewallRuleVerifier.Evaluate(
                firewall,
                SetupConstants.HttpsPort,
                request.ViewerIpv4).IsExact;
            steps.Add(new SetupStepResult(
                exactFirewall
                    ? "FIREWALL_EXACT"
                    : firewall.Exists
                        ? "FIREWALL_UPDATE_REQUIRED"
                        : "FIREWALL_NOT_INSTALLED",
                "방화벽 상태",
                exactFirewall ? SetupStepState.Succeeded : SetupStepState.Information,
                exactFirewall
                    ? $"Viewer {request.ViewerIpv4}/32 전용 HTTPS/18443 규칙이 정확합니다."
                    : firewall.Exists
                        ? "현재 규칙이 입력한 Viewer /32 또는 HTTPS/18443과 다릅니다. 설치/업데이트 시 안전한 규칙으로 교체합니다."
                    : "설치 시 Viewer 전용 방화벽 규칙을 만듭니다."));

            if (service.Running)
            {
                var ready = await healthProbe.WaitUntilReadyAsync(
                    new Uri("https://127.0.0.1:18443/health/ready"),
                    expectedProductVersion: null,
                    expectedProcessId: service.ProcessId,
                    TimeSpan.FromSeconds(5),
                    cancellationToken);
                steps.Add(new SetupStepResult(
                    ready ? "AGENT_READY" : "AGENT_NOT_READY",
                    "Agent 응답",
                    ready ? SetupStepState.Succeeded : SetupStepState.Information,
                    ready
                        ? "현재 Agent가 정상 응답합니다."
                        : "서비스는 실행 중이지만 준비 상태 응답을 받지 못했습니다."));
            }

            return SetupOperationResult.Success("사전 점검이 완료되었습니다.", steps);
        }
        catch (OperationCanceledException)
        {
            steps.Add(Failure(SetupErrorCodes.Cancelled, "사전 점검", "사용자가 작업을 취소했습니다."));
            return SetupOperationResult.Failure(
                SetupErrorCodes.Cancelled,
                "사전 점검이 취소되었습니다.",
                steps);
        }
        catch (SetupException exception)
        {
            steps.Add(Failure(exception.Code, "사전 점검", exception.Message));
            return SetupOperationResult.Failure(exception.Code, exception.Message, steps);
        }
        catch (Exception)
        {
            steps.Add(Failure(
                SetupErrorCodes.Unexpected,
                "사전 점검",
                "예상하지 못한 오류가 발생했습니다."));
            return SetupOperationResult.Failure(
                SetupErrorCodes.Unexpected,
                "사전 점검을 완료하지 못했습니다.",
                steps);
        }
    }

    internal static void ValidateInput(SetupRequest request)
    {
        if (!Ipv4Input.TryParseStrict(request.ViewerIpv4, out var viewer) ||
            !Ipv4Input.IsPrivate(viewer))
        {
            throw new SetupException(
                SetupErrorCodes.ViewerIpInvalid,
                "Viewer PC의 고정 사설 IPv4 주소를 입력하세요.");
        }

        if (request.TargetCidrs.Count is < 1 or > 2 ||
            request.TargetCidrs.Any(cidr => !IsCanonicalPrivateCidr(cidr)) ||
            request.TargetCidrs.Distinct(StringComparer.Ordinal).Count() !=
                request.TargetCidrs.Count)
        {
            throw new SetupException(
                SetupErrorCodes.NetworkSelectionInvalid,
                "스위치가 연결된 사설 관리망을 1~2개 선택하거나 추가하세요.");
        }
    }

    private static bool IsCanonicalPrivateCidr(string value) =>
        Ipv4Input.IsCanonicalPrivateCidr(value);

    private static SetupStepResult Success(string code, string label, string message) =>
        new(code, label, SetupStepState.Succeeded, message);

    internal static void AddFirewallWarnings(
        List<SetupStepResult> steps,
        FirewallSecurityAssessment assessment)
    {
        steps.AddRange(assessment.Warnings.Select(warning =>
            new SetupStepResult(
                warning.Code,
                "방화벽 중복 규칙",
                SetupStepState.Warning,
                warning.Message)));
    }

    private static SetupStepResult Failure(string code, string label, string message) =>
        new(code, label, SetupStepState.Failed, message);
}
