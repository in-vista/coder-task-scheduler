﻿using System;
using System.Threading.Tasks;
using GeeksCoreLibrary.Core.DependencyInjection.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WiserTaskScheduler.Core.Interfaces;
using WiserTaskScheduler.Core.Models;
using WiserTaskScheduler.Modules.Slack.modules;
using SlackNet;
using SlackNet.WebApi;

namespace WiserTaskScheduler.Core.Services
{
    public class SlackChatService : ISlackChatService, ISingletonService
    {
        private const string LogName = "SlackChatService";
        
        private readonly IServiceProvider serviceProvider;
        private readonly SlackSettings slackSettings;

        public SlackChatService(IServiceProvider serviceProvider, IOptions<WtsSettings> logSettings)
        {
            this.serviceProvider = serviceProvider;
            this.slackSettings = logSettings.Value.SlackSettings;
        }

#if DEBUG
        /// <inheritdoc />
        public async Task SendChannelMessageAsync(string message, string[] replies = null, string recipient = null)
        {
            return;
            // Only send messages to Slack for production Wiser Task Schedulers to prevent exceptions during developing/testing to trigger it.
        }
#else
        /// <inheritdoc />
        public async Task SendChannelMessageAsync(string message, string[] replies = null, string recipient = null)
        {
            if (slackSettings != null && !String.IsNullOrWhiteSpace(slackSettings.BotToken))
            {
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
                            Channel = recipient,
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