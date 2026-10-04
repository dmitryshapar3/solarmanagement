namespace DeyeSolar.Web.Billing;

// The authenticated request or circuit binds this once; installation ownership cannot substitute for it.
public sealed class CurrentBillingAccount
{
    public string? UserId { get; private set; }

    public void BindOnce(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > 450)
            throw new ArgumentException("A valid authenticated account is required.", nameof(userId));
        if (UserId is not null && UserId != userId)
            throw new InvalidOperationException("This request or circuit is already bound to another account.");
        UserId = userId;
    }
}
