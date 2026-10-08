namespace JRunner.Core.Contracts;

internal static class ContractText
{
    public static string RequireLowerKebabCase(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        var previousWasHyphen = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            var isLowercaseLetter = character is >= 'a' and <= 'z';
            var isDigit = character is >= '0' and <= '9';

            if (index == 0 && !isLowercaseLetter)
            {
                throw new ArgumentException("The value must start with a lowercase letter.", parameterName);
            }

            if (character == '-')
            {
                if (previousWasHyphen || index == value.Length - 1)
                {
                    throw new ArgumentException("The value must use single hyphens between segments.", parameterName);
                }

                previousWasHyphen = true;
                continue;
            }

            if (!isLowercaseLetter && !isDigit)
            {
                throw new ArgumentException("The value must use lower-kebab-case characters.", parameterName);
            }

            previousWasHyphen = false;
        }

        return value;
    }

    public static string RequireMessage(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }
}
