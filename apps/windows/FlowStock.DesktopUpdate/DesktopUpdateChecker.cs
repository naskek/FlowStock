using System.Net.Http.Json;

namespace FlowStock.DesktopUpdate;

public sealed class DesktopUpdateChecker
{
    private readonly HttpClient _httpClient;
    private readonly GitRepositoryClient _git;

    public DesktopUpdateChecker(HttpClient httpClient, GitRepositoryClient git)
    {
        _httpClient = httpClient;
        _git = git;
    }

    public async Task<DesktopUpdateCheckResult> CheckAsync(
        Uri updateServerBaseUri,
        string repositoryRoot,
        BuildIdentity installed,
        CancellationToken cancellationToken)
    {
        BuildIdentity? target = null;
        Uri? validatedUpdateServerBaseUri = null;
        try
        {
            validatedUpdateServerBaseUri = DesktopUpdateEndpointResolver.Validate(updateServerBaseUri);
            var versionUri = new Uri(validatedUpdateServerBaseUri, "/api/version");
            var response = await _httpClient.GetFromJsonAsync<ServerVersionResponse>(versionUri, cancellationToken)
                .ConfigureAwait(false);
            var manifest = response?.DesktopUpdate
                ?? throw new InvalidOperationException("Production server не публикует desktop update manifest.");
            target = manifest.ValidateAndGetTarget();
            if (response.ServerBuild == null
                || !string.Equals(response.ServerBuild.ProductVersion, target.ProductVersion, StringComparison.Ordinal)
                || !string.Equals(response.ServerBuild.SourceCommit, target.SourceCommit, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Server build identity и desktop target не совпадают.");
            }

            if (installed.SourceCommit == target.SourceCommit)
            {
                return new DesktopUpdateCheckResult(
                    DesktopUpdateState.Current,
                    installed,
                    target,
                    "Установлена актуальная версия.",
                    validatedUpdateServerBaseUri);
            }

            await _git.ValidateRepositoryAndRemoteAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
            await _git.FetchExpectedBranchAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
            await _git.ValidateTargetAsync(repositoryRoot, installed, target, cancellationToken).ConfigureAwait(false);
            var diagnostics = await _git.ReadDiagnosticsAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
            return new DesktopUpdateCheckResult(
                DesktopUpdateState.UpgradeAvailable,
                installed,
                target,
                "Доступно обновление FlowStock.",
                validatedUpdateServerBaseUri,
                diagnostics);
        }
        catch (ClientAheadException ex)
        {
            return new DesktopUpdateCheckResult(
                DesktopUpdateState.ClientAheadBlocked,
                installed,
                target,
                ex.Message,
                validatedUpdateServerBaseUri!);
        }
        catch (DivergedClientException ex)
        {
            return new DesktopUpdateCheckResult(
                DesktopUpdateState.DivergedBlocked,
                installed,
                target,
                ex.Message,
                validatedUpdateServerBaseUri!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DesktopUpdateCheckResult(
                DesktopUpdateState.Unavailable,
                installed,
                null,
                BuildUnavailableMessage(validatedUpdateServerBaseUri ?? updateServerBaseUri, ex),
                validatedUpdateServerBaseUri ?? updateServerBaseUri);
        }
    }

    private static string BuildUnavailableMessage(Uri endpoint, Exception exception)
    {
        var authority = endpoint.IsAbsoluteUri
            ? endpoint.GetLeftPart(UriPartial.Authority)
            : endpoint.ToString();
        return exception is HttpRequestException or TaskCanceledException
            ? $"Не удалось связаться с доверенным сервером обновлений {authority}: {exception.Message}"
            : $"Невозможно проверить обновление через доверенный сервер {authority}: {exception.Message}";
    }
}
