using System;
using System.Collections.Generic;

namespace FilePromptAIWin7
{
    internal enum InputKind
    {
        Text,
        Image,
        File
    }

    internal sealed class InputItem
    {
        public string Name { get; set; }
        public InputKind Kind { get; set; }
        public string TextContent { get; set; }
        public byte[] BinaryData { get; set; }
        public string MimeType { get; set; }
        public long OriginalBytes { get; set; }
        public string Note { get; set; }

        public string GetKindText()
        {
            if (Kind == InputKind.Image)
            {
                return "图片";
            }

            if (Kind == InputKind.File)
            {
                return "文件";
            }

            return "文本";
        }

        public string GetSizeText()
        {
            if (Kind == InputKind.Text)
            {
                int length = TextContent == null ? 0 : TextContent.Length;
                return length.ToString("N0") + " 字符";
            }

            int byteLength = BinaryData == null ? 0 : BinaryData.Length;
            if (byteLength >= 1024 * 1024)
            {
                return (byteLength / 1024d / 1024d).ToString("0.0") + " MB";
            }

            return (byteLength / 1024d).ToString("0.0") + " KB";
        }
    }

    internal sealed class ModelRequest
    {
        public string EndpointUrl { get; set; }
        public string ApiKey { get; set; }
        public string ModelName { get; set; }
        public string SystemPrompt { get; set; }
        public string Prompt { get; set; }
        public IList<InputItem> Attachments { get; set; }
        public IList<ConversationMessage> ConversationMessages { get; set; }
    }

    internal sealed class ModelCallException : Exception
    {
        public int StatusCode { get; private set; }
        public string RequestId { get; private set; }

        public ModelCallException(string message)
            : base(message)
        {
        }

        public ModelCallException(string message, int statusCode, string requestId)
            : base(message)
        {
            StatusCode = statusCode;
            RequestId = requestId;
        }
    }
}
