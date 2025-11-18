using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using YKT.AI.TotalApi;

namespace FileSharer.Controllers
{
    internal class ChatController
    {
        static IAiService aiService = new AiService();
        public static async void Init()
        {
            aiService.Init(new YKTInitPo() { Appkey = "sk-1ae18b8ce5fd4392b13e5a22ff41219a", EnumApiChannel = EnumApiChannel.Deepseek });
        }

        public static IObservable<string> Chat(string content)
        {
            return Observable.Create<string>(observer =>
            {
                return Task.Run(async () =>
                {
                    string _content = string.Empty;
                    //流式返回 
                    await foreach (var res in aiService.ChatYeildContentAsync(
                            new YKTResquestPo() { Title = content, enumApiChannel = EnumApiChannel.Deepseek, Model = "deepseek-chat", IsStream = true })
                    )
                    {
                        _content += res;
                        observer.OnNext(_content);
                    }
                    observer.OnCompleted();
                });
            });
        }
    }
}
