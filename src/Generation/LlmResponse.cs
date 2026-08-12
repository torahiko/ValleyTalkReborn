using System.Collections.Generic;

namespace ValleytalkReborn
{
    /// <summary>
    /// 封装单个结构化工具调用（来自 Native Function Calling 响应）
    /// </summary>
    internal class ToolCallData
    {
        public string FunctionName { get; set; }
        public string JsonArguments { get; set; }
    }

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
        /// 结构化工具调用列表（Native Function Calling 模式下填充）
        /// </summary>
        public List<ToolCallData> ToolCalls { get; set; } = new List<ToolCallData>();

        /// <summary>
        /// speak_in_bubble 工具被调用时置 true，通知上层跳过 DrawDialogue。
        /// </summary>
        public bool UsedBubble { get; set; } = false;

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
    }
}