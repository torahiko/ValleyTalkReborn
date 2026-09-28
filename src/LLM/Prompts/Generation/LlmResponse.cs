namespace ValleytalkReborn
{
    /// <summary>
    /// 封装 LLM API 返回的响应结果对象
    /// </summary>
    internal class LlmResponse
    {
        public string Text { get; set; }
        public string ErrorMessage { get; set; }
        public int ResponseCode { get; set; }
        public bool IsSuccess { get; set; }

        /// <summary>
        /// The reason this LLM response ended.
        /// </summary>
        public DialogueModels.LlmRequestEndReason EndReason { get; set; } = DialogueModels.LlmRequestEndReason.Success;

        /// <summary>
        /// 成功响应的构造函数
        /// </summary>
        public LlmResponse(string text, bool isSuccess = true)
        {
            Text = text;
            IsSuccess = isSuccess;
        }

        /// <summary>
        /// 失败/异常响应的构造函数
        /// </summary>
        public LlmResponse(string errorMessage, int responseCode, bool isSuccess = false)
        {
            ErrorMessage = errorMessage;
            ResponseCode = responseCode;
            IsSuccess = isSuccess;
        }

        /// <summary>
        /// Create a cancelled response.
        /// </summary>
        public static LlmResponse Cancelled()
        {
            return new LlmResponse(null, 0, false)
            {
                EndReason = DialogueModels.LlmRequestEndReason.Cancelled
            };
        }

        /// <summary>
        /// Create a timeout response.
        /// </summary>
        public static LlmResponse Timeout()
        {
            return new LlmResponse(null, 0, false)
            {
                EndReason = DialogueModels.LlmRequestEndReason.Timeout
            };
        }
    }
}