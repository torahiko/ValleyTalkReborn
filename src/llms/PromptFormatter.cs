namespace ValleytalkReborn;

public class PromptFormatter
{
    private readonly string _format;
    private readonly string _eot;

    public PromptFormatter(string format, string EoT)
    {
        _format = format;
        _eot = EoT;
    }

    public string Format(string system, string prompt)
    {
        // 增加容错：如果 format 为空，直接返回 prompt 避免报错
        if (string.IsNullOrEmpty(_format)) return prompt;
        return string.Format(_format, system, prompt);
    }

    // Strip quotation marks and EoT tokens from response
    public string Strip(string response)
    {
        if (string.IsNullOrEmpty(response)) return string.Empty;

        var cleanResponse = response.Replace("\"", "");
        
        if (!string.IsNullOrEmpty(_eot))
        {
            cleanResponse = cleanResponse.Replace(_eot, "");
        }
        
        return cleanResponse;
    }
}