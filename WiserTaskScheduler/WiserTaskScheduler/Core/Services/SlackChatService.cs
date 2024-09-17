using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using GeeksCoreLibrary.Core.DependencyInjection.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WiserTaskScheduler.Core.Interfaces;
using WiserTaskScheduler.Core.Models;
using SlackNet;
using SlackNet.WebApi;

namespace WiserTaskScheduler.Core.Services
{
    public class SlackChatService : ISlackChatService, ISingletonService
    {
        private const string LogName = "SlackChatService";
        
        private readonly IServiceProvider serviceProvider;
        private readonly WtsSettings wtsSettings;
        private readonly SlackSettings slackSettings;

        private ConcurrentDictionary<string, DateTime> sendMessages;

        public SlackChatService(IServiceProvider serviceProvider, IOptions<WtsSettings> wtsSettings)
        {
            this.serviceProvider = serviceProvider;
            this.wtsSettings = wtsSettings.Value;
            slackSettings = wtsSettings.Value.SlackSettings;
            sendMessages = new ConcurrentDictionary<string, DateTime>();
        }

#if DEBUG
        /// <inheritdoc />
        public async Task SendChannelMessageAsync(string message, string[] replies = null, string recipient = null, string messageHash = null)
        {
            return;
            // Only send messages to Slack for production Wiser Task Schedulers to prevent exceptions during developing/testing to trigger it.
        }
#else
        /// <inheritdoc />
        public async Task SendChannelMessageAsync(string message, string[] replies = null, string recipient = null, string messageHash = null)
        {
            if (slackSettings != null && !String.IsNullOrWhiteSpace(slackSettings.BotToken))
            {
                // If a hash is provided and the message has been sent within the set interval time, don't send it again. 30 seconds are added as a buffer.
                if (!String.IsNullOrWhiteSpace(messageHash))
                {
                    if (sendMessages.TryGetValue(messageHash, out var lastSendDate) && lastSendDate > DateTime.Now.AddMinutes(-wtsSettings.ErrorNotificationsIntervalInMinutes).AddSeconds(30))
                    {
                        return;
                    }

                    sendMessages.AddOrUpdate(messageHash, DateTime.Now, (key, oldValue) => DateTime.Now);
                }
                
                Message slackMessage = new Message
                {
                    Text = message,
                    Channel = recipient != null ? recipient : (slackSettings.Channel != null ? slackSettings.Channel : "" )
                };
                
                using var scope = serviceProvider.CreateScope();
                var slack = scope.ServiceProvider.GetRequiredService<ISlackApiClient>();

                var mainMessageSend = await slack.Chat.PostMessage(slackMessage);

                if (replies != null)
                {
                    foreach (var reply in replies)
                    {
                        Message replyMessage = new Message
                        {
                            Text = reply,
                            Channel = recipient != null ? recipient : (slackSettings.Channel != null ? slackSettings.Channel : "" ),
                            ThreadTs = mainMessageSend.Ts
                        };
                        
                        await slack.Chat.PostMessage(replyMessage);
                    }
                }
            }
        }
#endif
    }
}