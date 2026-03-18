using Azure.Identity;

namespace AzureWatcher.Services;

public class CredentialService
{
    private InteractiveBrowserCredential? _credential;

    public InteractiveBrowserCredential Get()
    {
        _credential ??= new InteractiveBrowserCredential();
        return _credential;
    }
}
