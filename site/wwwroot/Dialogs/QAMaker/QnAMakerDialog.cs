// 
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license.
// 
// Microsoft Bot Framework: http://botframework.com
// 
// Cognitive Services based Dialogs for Bot Builder:
// https://github.com/Microsoft/BotBuilder-CognitiveServices 
// 
// Copyright (c) Microsoft Corporation
// All rights reserved.
// 
// MIT License:
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Connector;
using Microsoft.Bot.Builder.Internals.Fibers;
using System.Reflection;
using System.Linq;
using System.Web;
using AdaptiveCards;

namespace Microsoft.Bot.Sample.QnABot.CognitiveServices.QnAMaker
{
    /// <summary>
    /// A dialog specialized to handle QnA response from QnA Maker.
    /// </summary>
    [Serializable]
    public class QnAMakerDialog : IDialog<IMessageActivity>
    {
        protected IQnAService[] services;
        private QnAMakerResults qnaMakerResults;
        private FeedbackRecord feedbackRecord;
        private const double QnAMakerHighConfidenceScoreThreshold = 0.90;
        private const double QnAMakerHighConfidenceDeltaThreshold = 0.30;

        public IQnAService[] MakeServicesFromAttributes()
        {


            QnAMakerAttribute att = new QnAMakerAttribute("e00dabb2-8106-4ce0-9cd2-2e2bc6fa8492", "9523cfd3-f441-472f-aa78-a42438b33cde", "default message",0.3,10, "https://qnairesqnamaker.azurewebsites.net");
            this.services = new QnAMakerService[] {new QnAMakerService(att) };
            return this.services;
            //var type = this.GetType();
            //var qnaModels = type.GetCustomAttributes<QnAMakerAttribute>(inherit: true);
            //return qnaModels.Select(m => new QnAMakerService(m)).Cast<IQnAService>().ToArray();

        }

        /// <summary>
        /// Construct the QnA Service dialog.
        /// </summary>
        /// <param name="services">The QnA service.</param>
        public QnAMakerDialog(params IQnAService[] services)
        {
            if (services.Length == 0)
            {
                services = MakeServicesFromAttributes();
            }

            SetField.NotNull(out this.services, nameof(services), services);
        }

        async Task IDialog<IMessageActivity>.StartAsync(IDialogContext context)
        {
            context.Wait(MessageReceivedAsync);
        }

        public async Task MessageReceivedAsync(IDialogContext context, IAwaitable<IMessageActivity> argument)
        {
            var message = await argument;
            
            if (message != null && !string.IsNullOrEmpty(message.Text))
            {
                var tasks = this.services.Select(s => s.QueryServiceAsync(message.Text)).ToArray();
                await Task.WhenAll(tasks);

                if (tasks.Any())
                {
                    var sendDefaultMessageAndWait = true;
                    qnaMakerResults = tasks.FirstOrDefault(x => x.Result.ServiceCfg != null)?.Result;
                    if (tasks.Count(x => x.Result.Answers?.Count > 0) > 0)
                    {
                        var maxValue = tasks.Max(x => x.Result.Answers[0].Score);
                        qnaMakerResults = tasks.First(x => x.Result.Answers[0].Score == maxValue).Result;

                        if (qnaMakerResults != null && qnaMakerResults.Answers != null && qnaMakerResults.Answers.Count > 0)
                        {
                            if (this.IsConfidentAnswer(qnaMakerResults))
                            {
                                await this.RespondFromQnAMakerResultAsync(context, message, qnaMakerResults);
                                await this.DefaultWaitNextMessageAsync(context, message, qnaMakerResults);
                            }
                            else
                            {
                                feedbackRecord = new FeedbackRecord { UserId = message.From.Id, UserQuestion = message.Text };
                                await this.QnAFeedbackStepAsync(context, qnaMakerResults);
                                //await context.PostAsync(FormatResponseMessage(context, qnaMakerResults.Answers));
                            }

                            sendDefaultMessageAndWait = false;
                        }
                    }

                    if (sendDefaultMessageAndWait)
                    {
                        await context.PostAsync(qnaMakerResults.ServiceCfg.DefaultMessage);
                        await this.DefaultWaitNextMessageAsync(context, message, qnaMakerResults);
                    }
                }
            }
        }

        protected virtual bool IsConfidentAnswer(QnAMakerResults qnaMakerResults)
        {
            if (qnaMakerResults.Answers.Count <= 1 || qnaMakerResults.Answers.First().Score >= QnAMakerHighConfidenceScoreThreshold)
            {
                return true;
            }

            if (qnaMakerResults.Answers[0].Score - qnaMakerResults.Answers[1].Score > QnAMakerHighConfidenceDeltaThreshold)
            {
                return true;
            }

            return false;
        }

        private async Task ResumeAndPostAnswer(IDialogContext context, IAwaitable<string> argument)
        {
            try
            {
                var selection = await argument;
                if (qnaMakerResults != null)
                {
                    bool match = false;
                    foreach (var qnaMakerResult in qnaMakerResults.Answers)
                    {
                        if (qnaMakerResult.Questions[0].Equals(selection, StringComparison.OrdinalIgnoreCase))
                        {
                            await context.PostAsync(qnaMakerResult.Answer);
                            match = true;

                            if (feedbackRecord != null)
                            {
                                feedbackRecord.KbQuestion = qnaMakerResult.Questions.First();
                                feedbackRecord.KbAnswer = qnaMakerResult.Answer;

                                var tasks =
                                    this.services.Select(
                                        s =>
                                        s.ActiveLearnAsync(
                                            feedbackRecord.UserId,
                                            feedbackRecord.UserQuestion,
                                            feedbackRecord.KbQuestion,
                                            feedbackRecord.KbAnswer,
                                            qnaMakerResults.ServiceCfg.KnowledgebaseId)).ToArray();

                                await Task.WhenAll(tasks);
                                break;
                            }
                        }
                    }
                }
            }
            catch (TooManyAttemptsException) { }
            await this.DefaultWaitNextMessageAsync(context, context.Activity.AsMessageActivity(), qnaMakerResults);
        }

        protected virtual async Task QnAFeedbackStepAsync(IDialogContext context, QnAMakerResults qnaMakerResults)
        {
            var qnaList = qnaMakerResults.Answers;
            var questions = qnaList.Select(x => x.Questions[0]).Concat(new[] {"None of the above"}).ToArray();

            PromptOptions<string> promptOptions = new PromptOptions<string>(
                prompt: "You may would like to find this?",
                tooManyAttempts: "Too many attempts",
                options: questions,
                attempts: 0);

            //PromptDialog.Choice(context: context, resume: ResumeAndPostAnswer, promptOptions: promptOptions);
            //PromptDialog.Choice(context: context, resume: ResumeAndPostAnswer,prompt: "You may would like to look for this?", promptStyle :PromptStyle.Auto,descriptions:questions);
            PromptDialog.Choice(context: context, resume: ResumeAndPostAnswer,prompt: "You may would like to look for this??",descriptions:questions,options:questions);

        }

      private IMessageActivity FormatResponseMessage(IDialogContext context, List<QnAMakerResult> answers)
        {
            var reply = context.MakeMessage();
            AdaptiveCard card = new AdaptiveCard();
            card.Body.Add(new AdaptiveTextBlock()
            {
                Text = answers[0].Questions[0],
                Weight = AdaptiveTextWeight.Bolder,
                Size = AdaptiveTextSize.Medium,
                Wrap = true
            });
            card.Body.Add(new AdaptiveTextBlock()
            {
                Text = answers[0].Answer,
                Wrap = true
            });

            card.Body.Add(new AdaptiveTextBlock()
            {
                Text = "Maybe you want to look for:",
                Weight = AdaptiveTextWeight.Default,
                Color = AdaptiveTextColor.Dark,
                Separator = true,
                Spacing = AdaptiveSpacing.Large,

            });

            for (int i = 1; i < answers.Count; i++)
            {
                AdaptiveColumnSet columnset = new AdaptiveColumnSet();
                columnset.Columns = new List<AdaptiveColumn>();
                AdaptiveColumn column = new AdaptiveColumn();
                column.Items.Add(new AdaptiveTextBlock()
                {
                    Text = string.Format("_{0}_", answers[i].Questions[0]),
                    Color = AdaptiveTextColor.Accent,
                    Wrap = true
                });
                column.SelectAction = new AdaptiveSubmitAction()
                {
                    Data = answers[i].Questions[0]
                };
                columnset.Columns.Add(column);
                card.Body.Add(columnset);
            }

            Attachment attachment = new Attachment()
            {
                ContentType = AdaptiveCard.ContentType,
                Content = card
            };

            reply.Attachments.Add(attachment);
            return reply;
        }
        
        protected virtual async Task RespondFromQnAMakerResultAsync(IDialogContext context, IMessageActivity message, QnAMakerResults result)
        {
            await context.PostAsync(result.Answers.First().Answer);
        }

        protected virtual async Task DefaultWaitNextMessageAsync(IDialogContext context, IMessageActivity message, QnAMakerResults result)
        {
            context.Done(true);
        }
    }
}
