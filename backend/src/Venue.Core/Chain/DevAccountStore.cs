using System.Collections.Concurrent;
using System.Numerics;
using Nethereum.Web3.Accounts;
using Venue.Chain;

namespace Venue.Chain;

/// <summary>
/// Dev-mode account provisioning (SEAM 1): on anvil there is no real Circle, so
/// `POST /v1/session {ref}` must map an email to a SIGNABLE, funded account the backend
/// holds a key for. The key is derived DETERMINISTICALLY from (ref, SaltSecret), so a
/// restart re-derives the same account (no persistent store, no stranding). The account
/// is funded with anvil gas at provision time. The gateway's user-key resolver consults
/// this store AFTER the configured DemoUsers (the /session/bind bridge).
/// </summary>
public sealed class DevAccountStore
{
    private readonly string _secret;
    private readonly ConcurrentDictionary<string, string> _byRef = new();
    private readonly ConcurrentDictionary<string, string> _byAddress = new();
    private IChainGateway? _gateway;

    public DevAccountStore(string secret)
    {
        _secret = secret;
    }

    /// <summary>The gateway is created after the store (the resolver needs both), so it is
    /// attached once available and used only for gas funding.</summary>
    public void AttachGateway(IChainGateway gateway)
    {
        _gateway = gateway;
    }

    public async Task<string> ProvisionAsync(string ref_, CancellationToken ct)
    {
        var refKey = ref_ ?? "";
        if (_byRef.TryGetValue(refKey, out var key))
            return AddressFromKey(key);

        key = DeriveKey(refKey);
        var address = AddressFromKey(key);
        _byRef[refKey] = key;
        _byAddress[address] = key;
        if (_gateway != null)
        {
            await _gateway.FundGasAsync(address, ct);
        }
        return address;
    }

    public string? KeyForAddress(string address)
        => _byAddress.TryGetValue(Domain.Addresses.Normalize(address), out var key) ? key : null;

    public string? AddressForRef(string ref_)
        => _byRef.TryGetValue(ref_ ?? "", out var key) ? AddressFromKey(key) : null;

    /// <summary>keccak256(ref + ":" + secret) is a 32-byte secp256k1 scalar (valid key with
    /// overwhelming probability) - deterministic per backend install, secret-mixed so refs
    /// alone cannot derive the key.</summary>
    private string DeriveKey(string ref_)
    {
        var material = System.Text.Encoding.UTF8.GetBytes(ref_ + ":" + _secret);
        var hash = Nethereum.Util.Sha3Keccack.Current.CalculateHash(material);
        return "0x" + Convert.ToHexStringLower(hash);
    }

    private static string AddressFromKey(string keyHex) => Domain.Addresses.Normalize(new Account(keyHex).Address);
}
