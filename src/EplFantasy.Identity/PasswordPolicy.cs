using Zxcvbn;

namespace EplFantasy.Identity;

/// <summary>
/// BR-285: "Password strength shall be evaluated using a strength-estimation mechanism rather than
/// fixed composition rules (e.g., mandatory symbol/number counts), with a minimum effective length
/// of twelve characters." zxcvbn is exactly that kind of mechanism — it scores guessability (0-4)
/// by pattern-matching against dictionaries, keyboard sequences, dates, and repeats, rather than
/// counting character classes. A password below the length floor is rejected outright regardless of
/// score; a score of 3+ ("safely unguessable" per zxcvbn's own scale) is the bar above the floor.
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 12;
    private const int MinimumScore = 3;

    /// <param name="password">The plaintext candidate — never persisted; the caller hashes it separately after this check passes.</param>
    /// <param name="userInputs">Values (username, email) zxcvbn penalizes the password for containing, since a password built from the account's own identifiers is trivially guessable regardless of its raw length/complexity.</param>
    public static bool Meets(string password, IEnumerable<string> userInputs)
    {
        if (password.Length < MinimumLength)
        {
            return false;
        }

        var result = Core.EvaluatePassword(password, userInputs);
        return result.Score >= MinimumScore;
    }
}
