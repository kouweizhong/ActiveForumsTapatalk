﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Security;
using CookComputing.XmlRpc;
using DotNetNuke.Entities.Users;
using DotNetNuke.Modules.ActiveForums;
using DotNetNuke.Modules.ActiveForumsTapatalk.Classes;
using DotNetNuke.Modules.ActiveForumsTapatalk.Structures;
using DotNetNuke.Modules.ActiveForumsTapatalk.Extensions;
using DotNetNuke.Security.Membership;
using DotNetNuke.Services.Social.Notifications;
using HtmlAgilityPack;

namespace DotNetNuke.Modules.ActiveForumsTapatalk.Handlers
{
    [XmlRpcService(Name = "ActiveForums.Tapatalk", Description = "Tapatalk Service For Active Forums", UseIntTag = true, AppendTimezoneOffset = true)]
    public class TapatalkAPIHandler : XmlRpcService
    {
        private enum ProcessModes { Normal, TextOnly, Quote }

        #region Forum API

        [XmlRpcMethod("get_config")]
        public XmlRpcStruct GetConfig()
        {
            var aftContext = ActiveForumsTapatalkModuleContext.Create(Context);

            if (aftContext == null || aftContext.Module == null)
                throw new XmlRpcFaultException(100, "Invalid Context"); 

            //if(aftContext.UserId < 0)
                Context.Response.AddHeader("Mobiquo_is_login", aftContext.UserId > 0 ? "true" : "false"); 

            var rpcstruct = new XmlRpcStruct
                                {
                                    {"sys_version", "0.0.2"},
                                    {"version", "dev"}, 
                                    {"is_open", aftContext.ModuleSettings.IsOpen}, 
                                    {"api_level", "3"},
                                    {"guest_okay", aftContext.ModuleSettings.AllowAnonymous},
                                    {"disable_bbcode", "0"},
                                    {"reg_url", "register.aspx"},
                                    {"charset", "UTF-8"},
                                    {"subscribe_forum", "1"},
                                    {"can_unread", "0"},
                                    {"announcement", "1"},
                                    {"conversation", "0"},
                                    {"inbox_stat", "0"}
                                };        

            return rpcstruct;

        }


        [XmlRpcMethod("get_forum")]
        public ForumStructure[] GetForums()
        {
            var aftContext = ActiveForumsTapatalkModuleContext.Create(Context);

            if (aftContext == null || aftContext.Module == null)
                throw new XmlRpcFaultException(100, "Invalid Context");

            Context.Response.AddHeader("Mobiquo_is_login", aftContext.UserId > 0 ? "true" : "false"); 

            var fc = new AFTForumController();
            var forumIds = fc.GetForumsForUser(aftContext.ForumUser.UserRoles, aftContext.Module.PortalID, aftContext.ModuleSettings.ForumModuleId, "CanRead");
            var forumTable = fc.GetForumView(aftContext.Module.PortalID, aftContext.ModuleSettings.ForumModuleId, aftContext.UserId, aftContext.ForumUser.IsSuperUser, forumIds);
            var forumSubscriptions =  fc.GetSubscriptionsForUser(aftContext.ModuleSettings.ForumModuleId, aftContext.UserId, null, 0).ToList();

            var result = new List<ForumStructure>();

            // Note that all the fields in the DataTable are strings if they come back from the cache, so they have to be converted appropriately.

            // Get the distict list of groups
            var groups = forumTable.AsEnumerable()
                .Select(r => new
                {
                    ID = Convert.ToInt32(r ["ForumGroupId"]), 
                    Name = r["GroupName"].ToString(), 
                    SortOrder = Convert.ToInt32(r["GroupSort"]),
                    Active = Convert.ToBoolean(r["GroupActive"])
                }).Distinct().Where(o => o.Active).OrderBy(o => o.SortOrder);

            // Get all forums the user can read
            var visibleForums = forumTable.AsEnumerable()
                .Select(f => new
                {
                    ID = Convert.ToInt32(f["ForumId"]),
                    ForumGroupId = Convert.ToInt32(f["ForumGroupId"]), 
                    Name = f["ForumName"].ToString(), 
                    Description = f["ForumDesc"].ToString(), 
                    ParentForumId = Convert.ToInt32(f["ParentForumId"]), 
                    ReadRoles = f["CanRead"].ToString(), 
                    SubscribeRoles = f["CanSubscribe"].ToString(),
                    LastRead = Convert.ToDateTime(f["LastRead"]),
                    LastPostDate = Convert.ToDateTime(f["LastPostDate"]),
                    SortOrder = Convert.ToInt32(f["ForumSort"]),
                    Active = Convert.ToBoolean(f["ForumActive"])
                })
                .Where(o => o.Active && ActiveForums.Permissions.HasPerm(o.ReadRoles, aftContext.ForumUser.UserRoles))
                .OrderBy(o => o.SortOrder).ToList();

            foreach(var group in groups)
            {
                // Find any root level forums for this group
                var groupForums = visibleForums.Where(vf => vf.ParentForumId == 0 && vf.ForumGroupId == group.ID).ToList();

                if(!groupForums.Any())
                    continue;

                // Create the structure to represent the group
                var groupStructure = new ForumStructure()
                {
                    ForumId =  "G" + group.ID.ToString(), // Append G to distinguish between forums and groups with the same id.
                    Name = group.Name.ToBytes(),
                    Description = null,
                    ParentId = "-1",
                    LogoUrl = null,
                    HasNewPosts = false,
                    IsProtected = false,
                    IsSubscribed = false,
                    CanSubscribe = false,
                    Url = null,
                    IsGroup = true,
                };
 
                // Add the Child Forums
                var groupChildren = new List<ForumStructure>();
                foreach(var groupForum in groupForums)
                {
                    var forumStructure = new ForumStructure
                    {
                        ForumId = groupForum.ID.ToString(),
                        Name = Utilities.StripHTMLTag(groupForum.Name).ToBytes(),
                        Description = Utilities.StripHTMLTag(groupForum.Description).ToBytes(),
                        ParentId = 'G' + group.ID.ToString(),
                        LogoUrl = null,
                        HasNewPosts = aftContext.UserId > 0 &&  groupForum.LastPostDate > groupForum.LastRead,
                        IsProtected = false,
                        IsSubscribed = forumSubscriptions.Any(fs => fs.ForumId == groupForum.ID),
                        CanSubscribe = ActiveForums.Permissions.HasPerm(groupForum.SubscribeRoles, aftContext.ForumUser.UserRoles),
                        Url = null,
                        IsGroup = false
                    };

                    

                    // Add any sub-forums

                    var subForums = visibleForums.Where(vf => vf.ParentForumId == groupForum.ID).ToList();

                    if (subForums.Any())
                    {
                        var forumChildren = new List<ForumStructure>();

                        foreach (var subForum in subForums)
                        {
                            forumChildren.Add(new ForumStructure
                            {
                                ForumId = subForum.ID.ToString(),
                                Name = Utilities.StripHTMLTag(subForum.Name).ToBytes(),
                                Description = Utilities.StripHTMLTag(subForum.Description).ToBytes(),
                                ParentId = groupForum.ID.ToString(),
                                LogoUrl = null,
                                HasNewPosts = aftContext.UserId > 0 && subForum.LastPostDate > subForum.LastRead,
                                IsProtected = false,
                                IsSubscribed = forumSubscriptions.Any(fs => fs.ForumId == subForum.ID),
                                CanSubscribe = ActiveForums.Permissions.HasPerm(subForum.SubscribeRoles, aftContext.ForumUser.UserRoles),
                                Url = null,
                                IsGroup = false
                            });
                        }

                        forumStructure.Children = forumChildren.ToArray();
                    }

                    groupChildren.Add(forumStructure);
                }

                groupStructure.Children = groupChildren.ToArray();

                result.Add(groupStructure);
            }

            return result.ToArray();
        }

        #endregion

        #region Topic API

        [XmlRpcMethod("get_topic")]
        public TopicListStructure  GetTopic(params object[] parameters)
        {
            if(parameters[0].ToString().StartsWith("G"))
            {
                var aftContext = ActiveForumsTapatalkModuleContext.Create(Context);

                if (aftContext == null || aftContext.Module == null)
                    throw new XmlRpcFaultException(100, "Invalid Context");

                Context.Response.AddHeader("Mobiquo_is_login", aftContext.UserId > 0 ? "true" : "false");

                return new TopicListStructure
                {
                    CanPost = false,
                    ForumId = parameters[0].ToString(),
                    ForumName = string.Empty.ToBytes(),
                    TopicCount = 0
                };
            }


            if (parameters.Length == 3)
                return GetTopic(Convert.ToInt32(parameters[0]), Convert.ToInt32(parameters[1]), Convert.ToInt32(parameters[2]));
            
            if (parameters.Length == 4)
                return GetTopic(Convert.ToInt32(parameters[0]), Convert.ToInt32(parameters[1]), Convert.ToInt32(parameters[2]), parameters[3].ToString());

            throw new XmlRpcFaultException(100, "Invalid Method Signature");
        }

        private TopicListStructure GetTopic(int forumId, int startIndex, int endIndex, string mode = null)
        {
            var aftContext = ActiveForumsTapatalkModuleContext.Create(Context);

            if (aftContext == null || aftContext.Module == null)
                throw new XmlRpcFaultException(100, "Invalid Context");

            Context.Response.AddHeader("Mobiquo_is_login", aftContext.UserId > 0 ? "true" : "false");

            var portalId = aftContext.Module.PortalID;
            var forumModuleId = aftContext.ModuleSettings.ForumModuleId;

            var fc = new AFTForumController();

            var fp = fc.GetForumPermissions(forumId);

            if (!ActiveForums.Permissions.HasPerm(aftContext.ForumUser.UserRoles, fp.CanRead))
                throw new XmlRpcFaultException(100, "No Read Permissions");

            var maxRows = endIndex + 1 - startIndex;

            var forumTopicsSummary = fc.GetForumTopicSummary(portalId, forumModuleId, forumId, aftContext.UserId, mode);
            var forumTopics = fc.GetForumTopics(portalId, forumModuleId, forumId, aftContext.UserId, startIndex, maxRows, mode);

            var canSubscribe = ActiveForums.Permissions.HasPerm(aftContext.ForumUser.UserRoles, fp.CanSubscribe);

            var mainSettings = new SettingsInfo { MainSettings = new Entities.Modules.ModuleController().GetModuleSettings(forumModuleId) };

            var profilePath = string.Format("{0}://{1}{2}", Context.Request.Url.Scheme, Context.Request.Url.Host, VirtualPathUtility.ToAbsolute("~/profilepic.ashx"));

            var forumTopicsStructure = new TopicListStructure
                                           {
                                               CanPost = ActiveForums.Permissions.HasPerm(aftContext.ForumUser.UserRoles, fp.CanCreate),
                                               ForumId = forumId.ToString(),
                                               ForumName = forumTopicsSummary.ForumName.ToBytes(),
                                               TopicCount = forumTopicsSummary.TopicCount,
                                               Topics = forumTopics.Select(t => new TopicStructure{ 
                                                   TopicId = t.TopicId.ToString(),
                                                   AuthorAvatarUrl = string.Format("{0}?userId={1}&w=64&h=64", profilePath, t.AuthorId),
                                                   AuthorName = GetAuthorName(mainSettings, t).ToBytes(),
                                                   CanSubscribe = canSubscribe,
                                                   ForumId = forumId.ToString(),
                                                   HasNewPosts = t.LastReplyId > t.UserLastReplyRead,
                                                   IsLocked = t.IsLocked,
                                                   IsSubscribed = t.SubscriptionType > 0,
                                                   LastReplyDate = t.LastReplyDate,
                                                   ReplyCount = t.ReplyCount,
                                                   Summary = GetSummary(t.Summary, t.Body).ToBytes(),
                                                   ViewCount = t.ViewCount,
                                                   Title = t.Subject.ToBytes()
                                               }).ToArray()
                                           };
                                             
                             

            return forumTopicsStructure;
        }

        [XmlRpcMethod("new_topic")]
        public XmlRpcStruct NewTopic(params object[] parameters)
        {
            if (parameters.Length < 3)
                throw new XmlRpcFaultException(100, "Invalid Method Signature");

            var forumId = Convert.ToInt32(parameters[0]);
            var subject = Encoding.Default.GetString((byte[]) parameters[1]);
            var body = Encoding.Default.GetString((byte[]) parameters[2]);

            var prefixId = parameters.Length >= 4 ? Convert.ToString(parameters[3]) : null;
            var attachmentIds = parameters.Length >= 5 ? (string[])parameters[4] : null;
            var groupId = parameters.Length >= 6 ? Convert.ToString(parameters[5]) : null;
            

            return NewTopic(forumId, subject, body, prefixId, attachmentIds, groupId);

        }

        private XmlRpcStruct NewTopic(int forumId, string subject, string body, string prefixId, string[] attachmentIds, string groupId)
        {
            var aftContext = ActiveForumsTapatalkModuleContext.Create(Context);
            
            Context.Response.AddHeader("Mobiquo_is_login", aftContext.UserId > 0 ? "true" : "false");

            var portalId = aftContext.Module.PortalID;
            var forumModuleId = aftContext.ModuleSettings.ForumModuleId;

            var fc = new AFTForumController();

            var forumInfo = fc.GetForum(portalId, forumModuleId, forumId);

            // Verify Post Permissions
            if(!ActiveForums.Permissions.HasPerm(forumInfo.Security.Create, aftContext.ForumUser.UserRoles))
            {
                return new XmlRpcStruct
                                {
                                    {"result", "false"}, //"true" for success
                                    {"result_text", "Not Authorized to Post".ToBytes()}, 
                                };
            }

            // Build User Permissions
            var canModApprove = ActiveForums.Permissions.HasPerm(forumInfo.Security.ModApprove, aftContext.ForumUser.UserRoles);
            var canTrust = ActiveForums.Permissions.HasPerm(forumInfo.Security.Trust, aftContext.ForumUser.UserRoles);
            var userProfile =  aftContext.UserId > 0 ? aftContext.ForumUser.Profile : new UserProfileInfo { TrustLevel = -1 };
            var userIsTrusted = Utilities.IsTrusted((int)forumInfo.DefaultTrustValue, userProfile.TrustLevel, canTrust, forumInfo.AutoTrustLevel, userProfile.PostCount);

            // Determine if the post should be approved
            var isApproved = !forumInfo.IsModerated || userIsTrusted || canModApprove;

            var mainSettings = new SettingsInfo {MainSettings = new Entities.Modules.ModuleController().GetModuleSettings(forumModuleId)};

            var dnnUser = Entities.Users.UserController.GetUserById(portalId, aftContext.UserId);

            var authorName = GetAuthorName(mainSettings, dnnUser);

            var themePath = string.Format("{0}://{1}{2}", Context.Request.Url.Scheme, Context.Request.Url.Host, VirtualPathUtility.ToAbsolute("~/DesktopModules/activeforums/themes/" + mainSettings.Theme + "/"));

            subject = Utilities.CleanString(portalId, subject, false, EditorTypes.TEXTBOX, forumInfo.UseFilter, false, forumModuleId, themePath, false);
            body = Utilities.CleanString(portalId, TapatalkToHtml(body), forumInfo.AllowHTML, EditorTypes.HTMLEDITORPROVIDER, forumInfo.UseFilter, false, forumModuleId, themePath, forumInfo.AllowEmoticons);

            // Create the topic

            var ti = new TopicInfo();
            var dt = DateTime.Now;
            ti.Content.DateCreated = dt;
            ti.Content.DateUpdated = dt;
            ti.Content.AuthorId = aftContext.UserId;
            ti.Content.AuthorName = authorName;
            ti.Content.IPAddress = Context.Request.UserHostAddress;
            ti.TopicUrl = string.Empty;
            ti.Content.Body = body;
            ti.Content.Subject = subject;
            ti.Content.Summary = string.Empty;

            ti.IsAnnounce = false;
            ti.IsPinned = false;
            ti.IsLocked = false;
            ti.IsDeleted = false;
            ti.IsArchived = false;

            ti.StatusId = -1;
            ti.TopicIcon = string.Empty;
            ti.TopicType = 0;

            ti.IsApproved = isApproved;

            // Save the topic
            var tc = new TopicsController();
            var topicId = tc.TopicSave(portalId, ti);
            ti = tc.Topics_Get(portalId, forumModuleId, topicId, forumId, -1, false);

            if(ti == null)
            {
                return new XmlRpcStruct
                                {
                                    {"result", "false"}, //"true" for success
                                    {"result_text", "Error Creating Post".ToBytes()}, 
                                };
            }

            // Update stats
            tc.Topics_SaveToForum(forumId, topicId, portalId, forumModuleId);
            if (ti.IsApproved && ti.Author.AuthorId > 0)
            {
                var uc = new ActiveForums.Data.Profiles();
                uc.Profile_UpdateTopicCount(portalId, ti.Author.AuthorId);
            }


            try
            {
                // Clear the cache
                var cachekey = string.Format("AF-FV-{0}-{1}", portalId, forumModuleId);
                DataCache.CacheClearPrefix(cachekey);

                // Subscribe the user if they have auto-subscribe set.
                if (userProfile.PrefSubscriptionType != SubscriptionTypes.Disabled && !(Subscriptions.IsSubscribed(portalId, forumModuleId, forumId, topicId, SubscriptionTypes.Instant, aftContext.UserId)))
                {
                    new SubscriptionController().Subscription_Update(portalId, forumModuleId, forumId, topicId, (int)userProfile.PrefSubscriptionType, aftContext.UserId, aftContext.ForumUser.UserRoles);
                }

                if(isApproved)
                {
                    // Send User Notifications
                    Subscriptions.SendSubscriptions(portalId, forumModuleId, aftContext.ModuleSettings.ForumTabId, forumInfo, topicId, 0, ti.Content.AuthorId);

                    // Add Journal entry
                    var forumTabId = aftContext.ModuleSettings.ForumTabId;
                    var fullURL = fc.BuildUrl(forumTabId, forumModuleId, forumInfo.ForumGroup.PrefixURL, forumInfo.PrefixURL, forumInfo.ForumGroupId, forumInfo.ForumID, topicId, ti.TopicUrl, -1, -1, string.Empty, 1, forumInfo.SocialGroupId);
                    new Social().AddTopicToJournal(portalId, forumModuleId, forumId, topicId, ti.Author.AuthorId, fullURL, ti.Content.Subject, string.Empty, ti.Content.Body, forumInfo.ActiveSocialSecurityOption, forumInfo.Security.Read, forumInfo.SocialGroupId);
                }
                else
                {
                    // Send Mod Notifications
                    var mods = Utilities.GetListOfModerators(portalId, forumId);
                    var notificationType = NotificationsController.Instance.GetNotificationType("AF-ForumModeration");
                    var notifySubject = Utilities.GetSharedResource("NotificationSubjectTopic");
                    notifySubject = notifySubject.Replace("[DisplayName]", dnnUser.DisplayName);
                    notifySubject = notifySubject.Replace("[TopicSubject]", ti.Content.Subject);
                    var notifyBody = Utilities.GetSharedResource("NotificationBodyTopic");
                    notifyBody = notifyBody.Replace("[Post]", ti.Content.Body);
                    var notificationKey = string.Format("{0}:{1}:{2}:{3}:{4}", aftContext.ModuleSettings.ForumTabId, forumModuleId, forumId, topicId, 0);

                    var notification = new Notification
                    {
                        NotificationTypeID = notificationType.NotificationTypeId,
                        Subject = notifySubject,
                        Body = notifyBody,
                        IncludeDismissAction = false,
                        SenderUserID = dnnUser.UserID,
                        Context = notificationKey
                    };

                    NotificationsController.Instance.SendNotification(notification, portalId, null, mods);
                }
 
            }
            catch(Exception ex)
            {
                Services.Exceptions.Exceptions.LogException(ex); 
            }


            var result = new XmlRpcStruct
            {
                {"result", true}, //"true" for success
                {"result_text", "OK".ToBytes()}, 
                {"topic_id", ti.TopicId.ToString()},
            };

            if(!isApproved)
                result.Add("state", 1);

            return result;

        }

        #endregion

        #region Post API

        [XmlRpcMethod("get_thread")]
        public PostListStructure GetThread(params object[] parameters)
        {
            if (parameters.Length == 3)
                return GetThread(Convert.ToInt32(parameters[0]), Convert.ToInt32(parameters[1]), Convert.ToInt32(parameters[2]), false);

            if (parameters.Length == 4)
                return GetThread(Convert.ToInt32(parameters[0]), Convert.ToInt32(parameters[1]), Convert.ToInt32(parameters[2]), Convert.ToBoolean(parameters[3]));

            throw new XmlRpcFaultException(100, "Invalid Method Signature");
        }

        private PostListStructure GetThread(int topicId, int startIndex, int endIndex, bool returnHtml)
        {
            var aftContext = ActiveForumsTapatalkModuleContext.Create(Context);

            if (aftContext == null || aftContext.Module == null)
                throw new XmlRpcFaultException(100, "Invalid Context");

            Context.Response.AddHeader("Mobiquo_is_login", aftContext.UserId > 0 ? "true" : "false");

            var portalId = aftContext.Module.PortalID;
            var forumModuleId = aftContext.ModuleSettings.ForumModuleId;

            var fc = new AFTForumController();

            var forumId = fc.GetTopicForumId(topicId);

            if(forumId <= 0)
                throw new XmlRpcFaultException(100, "Invalid Topic");

            var fp = fc.GetForumPermissions(forumId);

            if (!ActiveForums.Permissions.HasPerm(aftContext.ForumUser.UserRoles, fp.CanRead))
                throw new XmlRpcFaultException(100, "No Read Permissions");

            var maxRows = endIndex + 1 - startIndex;

            var forumPostSummary = fc.GetForumPostSummary(aftContext.Module.PortalID, aftContext.ModuleSettings.ForumModuleId, forumId, topicId, aftContext.UserId);
            var forumPosts = fc.GetForumPosts(aftContext.Module.PortalID, aftContext.ModuleSettings.ForumModuleId, forumId, topicId, aftContext.UserId, startIndex, maxRows);

            var breadCrumbs = new List<BreadcrumbStructure>
                                  {
                                      new BreadcrumbStructure
                                          {
                                              ForumId = 'G' + forumPostSummary.ForumGroupId.ToString(),
                                              IsCategory = true,
                                              Name = forumPostSummary.GroupName.ToBytes()
                                          },
                                  };

            // If we're in a sub forum, add the parent to the breadcrumb
            if(forumPostSummary.ParentForumId > 0)
                breadCrumbs.Add(new BreadcrumbStructure
                {
                    ForumId = forumPostSummary.ParentForumId.ToString(),
                    IsCategory = false,
                    Name = forumPostSummary.ParentForumName.ToBytes()
                });

            breadCrumbs.Add(new BreadcrumbStructure
            {
                ForumId = forumId.ToString(),
                IsCategory = false,
                Name = forumPostSummary.ForumName.ToBytes()
            });

            var mainSettings = new SettingsInfo { MainSettings = new Entities.Modules.ModuleController().GetModuleSettings(forumModuleId) };

            var profilePath = string.Format("{0}://{1}{2}", Context.Request.Url.Scheme, Context.Request.Url.Host, VirtualPathUtility.ToAbsolute("~/profilepic.ashx"));

            var result = new PostListStructure
                             { 
                                 PostCount = forumPostSummary.ReplyCount + 1,
                                 CanReply = ActiveForums.Permissions.HasPerm(aftContext.ForumUser.UserRoles, fp.CanReply),
                                 CanSubscribe = ActiveForums.Permissions.HasPerm(aftContext.ForumUser.UserRoles, fp.CanSubscribe),
                                 ForumId = forumId,
                                 ForumName = forumPostSummary.ForumName.ToBytes(),
                                 IsLocked = forumPostSummary.IsLocked,
                                 IsSubscribed = forumPostSummary.SubscriptionType > 0,
                                 Subject = forumPostSummary.Subject.ToBytes(),
                                 TopicId = topicId,
                                 Posts = forumPosts.Select(p => new PostStructure
                                    {
                                          PostID = p.ContentId.ToString(),
                                          AuthorAvatarUrl = string.Format("{0}?userId={1}&w=64&h=64", profilePath, p.AuthorId),
                                          AuthorName = GetAuthorName(mainSettings, p).ToBytes(),
                                          Body =  HtmlToTapatalk(p.Body, returnHtml).ToBytes(),
                                          CanEdit = false, // TODO: Fix this
                                          IsOnline = p.IsUserOnline,
                                          PostDate = p.DateCreated,
                                          Subject = p.Subject.ToBytes()
                                    }).ToArray(),
                                 Breadcrumbs = breadCrumbs.ToArray()
                              
                             };

            return result;
        }


        [XmlRpcMethod("get_quote_post")]
        public XmlRpcStruct GetQuote(params object[] parameters)
        {
            if (parameters.Length >= 1)
                return GetQuote(Convert.ToInt32(parameters[0]));

            throw new XmlRpcFaultException(100, "Invalid Method Signature");
        }

        private XmlRpcStruct GetQuote(int contentId)
        {
            var aftContext = ActiveForumsTapatalkModuleContext.Create(Context);

            if (aftContext == null || aftContext.Module == null)
                throw new XmlRpcFaultException(100, "Invalid Context");

            Context.Response.AddHeader("Mobiquo_is_login", aftContext.UserId > 0 ? "true" : "false");

            var portalId = aftContext.Module.PortalID;
            var forumModuleId = aftContext.ModuleSettings.ForumModuleId;

            var fc = new AFTForumController();

            // Retrieve the forum post
            var forumPost = fc.GetForumPost(portalId, forumModuleId, contentId);

            if(forumPost == null)
                throw new XmlRpcFaultException(100, "Bad Request");

            // Verify read permissions
            var fp = fc.GetForumPermissions(forumPost.ForumId);

            if (!ActiveForums.Permissions.HasPerm(aftContext.ForumUser.UserRoles, fp.CanRead))
                throw new XmlRpcFaultException(100, "No Read Permissions");

            // Load our forum settings
            var mainSettings = new SettingsInfo { MainSettings = new Entities.Modules.ModuleController().GetModuleSettings(forumModuleId) };

            // Build our sanitized quote
            var postedByTemplate = Utilities.GetSharedResource("[RESX:PostedBy]") + " {0} {1} {2}";

            var postedBy = string.Format(postedByTemplate, GetAuthorName(mainSettings, forumPost), Utilities.GetSharedResource("On.Text"), GetServerDateTime(mainSettings, forumPost.DateCreated));
            var result = HtmlToTapatalkQuote(postedBy, forumPost.Body);

            // Return the result
            return new XmlRpcStruct
            {
                {"post_id", contentId.ToString()},
                {"post_title", ("RE: " + forumPost.Subject).ToBytes()}, 
                {"post_content", result.ToBytes()}
            };
        }

        [XmlRpcMethod("reply_post")]
        public XmlRpcStruct Reply(params object[] parameters)
        {
            if (parameters.Length < 4)
                throw new XmlRpcFaultException(100, "Invalid Method Signature");

            var forumId = Convert.ToInt32(parameters[0]);
            var topicId = Convert.ToInt32(parameters[1]);
            var subject = Encoding.Default.GetString((byte[]) parameters[2]);
            var body = Encoding.Default.GetString((byte[]) parameters[3]);

            var attachmentIds = parameters.Length >= 5 ? (string[]) parameters[4] : null;
            var groupId = parameters.Length >= 6 ? Convert.ToString(parameters[5]) : null;
            var returnHtml = parameters.Length >= 7 ? Convert.ToBoolean(parameters[6]) : false;

            return Reply(forumId, topicId, subject, body, attachmentIds, groupId, returnHtml);
        }

        private XmlRpcStruct Reply(int forumId, int topicId, string subject, string body, string[] attachmentIds, string groupID, bool returnHtml)
        {
            var aftContext = ActiveForumsTapatalkModuleContext.Create(Context);

            Context.Response.AddHeader("Mobiquo_is_login", aftContext.UserId > 0 ? "true" : "false");

            var portalId = aftContext.Module.PortalID;
            var forumModuleId = aftContext.ModuleSettings.ForumModuleId;

            var fc = new AFTForumController();

            var forumInfo = fc.GetForum(portalId, forumModuleId, forumId);

            // Verify Post Permissions
            if (!ActiveForums.Permissions.HasPerm(forumInfo.Security.Reply, aftContext.ForumUser.UserRoles))
            {
                return new XmlRpcStruct
                                {
                                    {"result", "false"}, //"true" for success
                                    {"result_text", "Not Authorized to Reply".ToBytes()}, 
                                };
            }

            // Build User Permissions
            var canModApprove = ActiveForums.Permissions.HasPerm(forumInfo.Security.ModApprove, aftContext.ForumUser.UserRoles);
            var canTrust = ActiveForums.Permissions.HasPerm(forumInfo.Security.Trust, aftContext.ForumUser.UserRoles);
            var canDelete = ActiveForums.Permissions.HasPerm(forumInfo.Security.Delete, aftContext.ForumUser.UserRoles);
            var canModDelete = ActiveForums.Permissions.HasPerm(forumInfo.Security.ModDelete, aftContext.ForumUser.UserRoles);
            var canEdit = ActiveForums.Permissions.HasPerm(forumInfo.Security.Edit, aftContext.ForumUser.UserRoles);
            var canModEdit = ActiveForums.Permissions.HasPerm(forumInfo.Security.ModEdit, aftContext.ForumUser.UserRoles);

            var userProfile = aftContext.UserId > 0 ? aftContext.ForumUser.Profile : new UserProfileInfo { TrustLevel = -1 };
            var userIsTrusted = Utilities.IsTrusted((int)forumInfo.DefaultTrustValue, userProfile.TrustLevel, canTrust, forumInfo.AutoTrustLevel, userProfile.PostCount);

            // Determine if the post should be approved
            var isApproved = !forumInfo.IsModerated || userIsTrusted || canModApprove;

            var mainSettings = new SettingsInfo { MainSettings = new Entities.Modules.ModuleController().GetModuleSettings(forumModuleId) };

            var dnnUser = Entities.Users.UserController.GetUserById(portalId, aftContext.UserId);

            var authorName = GetAuthorName(mainSettings, dnnUser);

            var themePath = string.Format("{0}://{1}{2}", Context.Request.Url.Scheme, Context.Request.Url.Host, VirtualPathUtility.ToAbsolute("~/DesktopModules/activeforums/themes/" + mainSettings.Theme + "/"));

            subject = Utilities.CleanString(portalId, subject, false, EditorTypes.TEXTBOX, forumInfo.UseFilter, false, forumModuleId, themePath, false);
            body = Utilities.CleanString(portalId, TapatalkToHtml(body), forumInfo.AllowHTML, EditorTypes.HTMLEDITORPROVIDER, forumInfo.UseFilter, false, forumModuleId, themePath, forumInfo.AllowEmoticons);

            var dt = DateTime.Now;

            var ri = new ReplyInfo();
            ri.Content.DateCreated = dt;
            ri.Content.DateUpdated = dt;
            ri.Content.AuthorId = aftContext.UserId;
            ri.Content.AuthorName = authorName;
            ri.Content.IPAddress = Context.Request.UserHostAddress;
            ri.Content.Subject = subject;
            ri.Content.Summary = string.Empty;
            ri.Content.Body = body;
            ri.TopicId = topicId;
            ri.IsApproved = isApproved;
            ri.IsDeleted = false;
            ri.StatusId = -1;

            // Save the topic
            var rc = new ReplyController();
            var replyId = rc.Reply_Save(portalId, ri);
            ri = rc.Reply_Get(portalId, forumModuleId, topicId, replyId);

            if (ri == null)
            {
                return new XmlRpcStruct
                                {
                                    {"result", "false"}, //"true" for success
                                    {"result_text", "Error Creating Post".ToBytes()}, 
                                };
            }

            try
            {
                // Clear the cache
                var cachekey = string.Format("AF-FV-{0}-{1}", portalId, forumModuleId);
                DataCache.CacheClearPrefix(cachekey);

                // Subscribe the user if they have auto-subscribe set.
                if (userProfile.PrefSubscriptionType != SubscriptionTypes.Disabled && !(Subscriptions.IsSubscribed(portalId, forumModuleId, forumId, topicId, SubscriptionTypes.Instant, aftContext.UserId)))
                {
                    new SubscriptionController().Subscription_Update(portalId, forumModuleId, forumId, topicId, (int)userProfile.PrefSubscriptionType, aftContext.UserId, aftContext.ForumUser.UserRoles);
                }

                if (isApproved)
                {
                    // Send User Notifications
                    Subscriptions.SendSubscriptions(portalId, forumModuleId, aftContext.ModuleSettings.ForumTabId, forumInfo, topicId, ri.ReplyId, ri.Content.AuthorId);

                    // Add Journal entry
                    var forumTabId = aftContext.ModuleSettings.ForumTabId;
                    var ti = new TopicsController().Topics_Get(portalId, forumModuleId, topicId, forumId, -1, false);
                    var fullURL = fc.BuildUrl(forumTabId, forumModuleId, forumInfo.ForumGroup.PrefixURL, forumInfo.PrefixURL, forumInfo.ForumGroupId, forumInfo.ForumID, topicId, ti.TopicUrl, -1, -1, string.Empty, 1, forumInfo.SocialGroupId);
                    new Social().AddReplyToJournal(portalId, forumModuleId, forumId, topicId, ri.ReplyId, ri.Author.AuthorId, fullURL, ri.Content.Subject, string.Empty, ri.Content.Body, forumInfo.ActiveSocialSecurityOption, forumInfo.Security.Read, forumInfo.SocialGroupId);
                }
                else
                {
                    // Send Mod Notifications
                    var mods = Utilities.GetListOfModerators(portalId, forumId);
                    var notificationType = NotificationsController.Instance.GetNotificationType("AF-ForumModeration");
                    var notifySubject = Utilities.GetSharedResource("NotificationSubjectReply");
                    notifySubject = notifySubject.Replace("[DisplayName]", dnnUser.DisplayName);
                    notifySubject = notifySubject.Replace("[TopicSubject]", ri.Content.Subject);
                    var notifyBody = Utilities.GetSharedResource("NotificationBodyReply");
                    notifyBody = notifyBody.Replace("[Post]", ri.Content.Body);
                    var notificationKey = string.Format("{0}:{1}:{2}:{3}:{4}", aftContext.ModuleSettings.ForumTabId, forumModuleId, forumId, topicId, replyId);

                    var notification = new Notification
                    {
                        NotificationTypeID = notificationType.NotificationTypeId,
                        Subject = notifySubject,
                        Body = notifyBody,
                        IncludeDismissAction = false,
                        SenderUserID = dnnUser.UserID,
                        Context = notificationKey
                    };

                    NotificationsController.Instance.SendNotification(notification, portalId, null, mods);
                }

            }
            catch (Exception ex)
            {
                Services.Exceptions.Exceptions.LogException(ex);
            }


            var result = new XmlRpcStruct
            {
                {"result", true}, //"true" for success
                {"result_text", "OK".ToBytes()}, 
                {"post_id", ri.ContentId.ToString()},
                {"post_content", HtmlToTapatalk(ri.Content.Body, returnHtml).ToBytes() },
                {"can_edit", canEdit || canModEdit },
                {"can_delete", canDelete || canModDelete },
                {"post_time", dt},
                {"attachments", new {}}
            };

            if(!isApproved)
                result.Add("state", 1);

            return result;


        }


        #endregion

        #region User API

        [XmlRpcMethod("login")]
        public XmlRpcStruct Login(params object[] parameters)
        {
            if (parameters.Length < 2)
                throw new XmlRpcFaultException(100, "Invalid Method Signature"); 

            var login = Encoding.Default.GetString((byte[]) parameters[0]);
            var password = Encoding.Default.GetString((byte[]) parameters[1]);

            return Login(login, password);
        }

        public XmlRpcStruct Login(string login, string password)
        {
            var aftContext = ActiveForumsTapatalkModuleContext.Create(Context);

            if(aftContext == null || aftContext.Portal == null)
                throw new XmlRpcFaultException(100, "Invalid Context"); 

            var loginStatus = UserLoginStatus.LOGIN_FAILURE;

            Entities.Users.UserController.ValidateUser(aftContext.Portal.PortalID, login, password, string.Empty, aftContext.Portal.PortalName, Context.Request.UserHostAddress, ref loginStatus);

            var result = false;
            var resultText = string.Empty;

            switch(loginStatus)
            {
                case UserLoginStatus.LOGIN_SUCCESS:
                case UserLoginStatus.LOGIN_SUPERUSER:
                    result = true;
                    break;

                case UserLoginStatus.LOGIN_FAILURE:
                    resultText = "Invalid Login/Password Combination";
                    break;

                case UserLoginStatus.LOGIN_USERNOTAPPROVED:
                    resultText = "User Not Approved";
                    break;

                case UserLoginStatus.LOGIN_USERLOCKEDOUT:
                    resultText = "User Temporarily Locked Out";
                    break;

                default:
                    resultText = "Unknown Login Error";
                    break;
            }


            if(result)
            { 
                // Get the User
                var userInfo = Entities.Users.UserController.GetUserByName(aftContext.Module.PortalID, login);

                if(userInfo == null)
                {
                    result = false;
                    resultText = "Unknown Login Error";
                }
                else
                {
                    // Set Login Cookie
                    var expiration = DateTime.Now.Add(FormsAuthentication.Timeout); 

                    var ticket = new FormsAuthenticationTicket(1, login, DateTime.Now, expiration, false, userInfo.UserID.ToString());
                    var authCookie = new HttpCookie(aftContext.AuthCookieName, FormsAuthentication.Encrypt(ticket))
                    {
                        Domain = FormsAuthentication.CookieDomain,
                        Path = FormsAuthentication.FormsCookiePath,
                    };


                    Context.Response.SetCookie(authCookie);
                }
            }

            Context.Response.AddHeader("Mobiquo_is_login", result ? "true" : "false"); 

            var rpcstruct = new XmlRpcStruct
                                {
                                    {"result", result },
                                    {"result_text", resultText.ToBytes()}, 
                                    {"can_upload_avatar", false}
                                };
          

            return rpcstruct;

        }

        [XmlRpcMethod("logout_user")]
        public void Logout()
        {
            var aftContext = ActiveForumsTapatalkModuleContext.Create(Context);

            if (aftContext == null || aftContext.Portal == null)
                throw new XmlRpcFaultException(100, "Invalid Context");

            Context.Response.AddHeader("Mobiquo_is_login", "false");

            var authCookie = new HttpCookie(aftContext.AuthCookieName, string.Empty)
                            {
                                Expires = DateTime.Now.AddDays(-1)
                            };


            Context.Response.SetCookie(authCookie);
        }

        #endregion

        #region Subscribe API

        [XmlRpcMethod("subscribe_forum")]
        public XmlRpcStruct SubscribeForum(params object[] parameters)
        {
            if (parameters.Length < 1)
                throw new XmlRpcFaultException(100, "Invalid Method Signature"); 

            var forumId = Convert.ToInt32(parameters[0]);

            return Subscribe(forumId, null, false);
        }

        [XmlRpcMethod("subscribe_topic")]
        public XmlRpcStruct SubscribeTopic(params object[] parameters)
        {
            if (parameters.Length < 1)
                throw new XmlRpcFaultException(100, "Invalid Method Signature"); 

            var topicId = Convert.ToInt32(parameters[0]);

            return Subscribe(null, topicId, false);
        }

        [XmlRpcMethod("unsubscribe_forum")]
        public XmlRpcStruct UnsubscribeForum(params object[] parameters)
        {
            if (parameters.Length < 1)
                throw new XmlRpcFaultException(100, "Invalid Method Signature"); 

            var forumId = Convert.ToInt32(parameters[0]);

            return Subscribe(forumId, null, true);
        }

        [XmlRpcMethod("unsubscribe_topic")]
        public XmlRpcStruct UnsubscribeTopic(params object[] parameters)
        {
            if (parameters.Length < 1)
                throw new XmlRpcFaultException(100, "Invalid Method Signature"); 

            var topicId = Convert.ToInt32(parameters[0]);

            return Subscribe(null, topicId, true);
        }

        private XmlRpcStruct Subscribe(int? forumId, int? topicId, bool unsubscribe)
        {
            var aftContext = ActiveForumsTapatalkModuleContext.Create(Context);

            if (aftContext == null || aftContext.Module == null)
                throw new XmlRpcFaultException(100, "Invalid Context");

            Context.Response.AddHeader("Mobiquo_is_login", aftContext.UserId > 0 ? "true" : "false");

            if (!forumId.HasValue && !topicId.HasValue)
                return new XmlRpcStruct{{"result", "0"},{"result_text", "Bad Request".ToBytes()}};


            var portalId = aftContext.Module.PortalID;
            var forumModuleId = aftContext.ModuleSettings.ForumModuleId;

            // Look up the forum Id if needed
            if(!forumId.HasValue)
            {
                var ti = new TopicsController().Topics_Get(portalId, forumModuleId, topicId.Value);
                if(ti == null)
                    return new XmlRpcStruct { { "result", false }, { "result_text", "Topic Not Found".ToBytes() } };

                var post = new AFTForumController().GetForumPost(portalId, forumModuleId, ti.ContentId);
                if(post == null)
                    return new XmlRpcStruct { { "result", false }, { "result_text", "Topic Post Not Found".ToBytes() } };

                forumId = post.ForumId;
            }

            var subscriptionType = unsubscribe ? SubscriptionTypes.Disabled : SubscriptionTypes.Instant;

            var sub = new SubscriptionController().Subscription_Update(portalId, forumModuleId, forumId.Value, topicId.HasValue ? topicId.Value : -1, (int)subscriptionType, aftContext.UserId, aftContext.ForumUser.UserRoles);

            var result = (sub >= 0) ? "1" : "0";

            return new XmlRpcStruct
            {
                {"result", result},
                {"result_text", string.Empty.ToBytes()}, 
            };
        }
    

        #endregion


        #region Private Helper Methods

        private static string GetSummary(string summary, string body)
        {
            var result = !string.IsNullOrWhiteSpace(summary) ? summary : body;

            result = result + string.Empty;

            result = Utilities.StripHTMLTag(result);

            result = result.Length > 200 ? result.Substring(0, 200) : result;

            return result.Trim();
        }

        private static string TapatalkToHtml(string input)
        {
            input = input.Replace("<", "&lt;");
            input = input.Replace(">", "&gt;");
            
            input = input.Trim(new [] {' ', '\n', '\r', '\t'}).Replace("\n", "<br />");

            input = Regex.Replace(input, @"\[quote\=\'([^\]]+?)\'\]", "<blockquote class='afQuote'><span class='afQuoteTitle'>$1</span><br />", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            input = Regex.Replace(input, @"\[quote\=\""([^\]]+?)\""\]", "<blockquote class='afQuote'><span class='afQuoteTitle'>$1</span><br />", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            input = Regex.Replace(input, @"\[quote\=([^\]]+?)\]",  "<blockquote class='afQuote'><span class='afQuoteTitle'>$1</span><br />", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            input = Regex.Replace(input, @"\[quote\]", "<blockquote class='afQuote'>", RegexOptions.IgnoreCase);
            input = Regex.Replace(input, @"\[\/quote\]", "</blockquote>", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            input = Regex.Replace(input, @"\[img\](.+?)\[\/img\]", "<img src='$1' />", RegexOptions.IgnoreCase);
            input = Regex.Replace(input, @"\[url=\'(.+?)\'\](.+?)\[\/url\]", "<a href='$1'>$2</a>", RegexOptions.IgnoreCase);
            input = Regex.Replace(input, @"\[url=\""(.+?)\""\](.+?)\[\/url\]", "<a href='$1'>$2</a>", RegexOptions.IgnoreCase);
            input = Regex.Replace(input, @"\[url=(.+?)\](.+?)\[\/url\]", "<a href='$1'>$2</a>", RegexOptions.IgnoreCase);
            input = Regex.Replace(input, @"\[url\](.+?)\[\/url\]", "<a href='$1'>$1</a>", RegexOptions.IgnoreCase);
            input = Regex.Replace(input, @"\[(\/)?b\]", "<$1strong>", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            input = Regex.Replace(input, @"\[(\/)?i\]", "<$1i>", RegexOptions.IgnoreCase | RegexOptions.Multiline);

            return input;
        }

        private static string HtmlToTapatalk(string input, bool returnHtml)
        {
            if (string.IsNullOrWhiteSpace(input))
                return input;

            input = Regex.Replace(input, @"\s+", " ", RegexOptions.Multiline);

            var htmlBlock = new HtmlDocument();
            htmlBlock.LoadHtml(input);

            var tapatalkMarkup = new StringBuilder();

            ProcessNode(tapatalkMarkup, htmlBlock.DocumentNode, ProcessModes.Normal, returnHtml);

            return tapatalkMarkup.ToString().Trim(new[] { ' ', '\n', '\r', '\t' });
        }

        private static string HtmlToTapatalkQuote(string postedBy, string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return input;

            input = Regex.Replace(input, @"\s+", " ", RegexOptions.Multiline);

            var htmlBlock = new HtmlDocument();
            htmlBlock.LoadHtml(input);

            var tapatalkMarkup = new StringBuilder();

            ProcessNode(tapatalkMarkup, htmlBlock.DocumentNode, ProcessModes.Quote, false);

            return string.Format("[quote={0}]\r\n{1}\r\n[/quote]\r\n", postedBy, tapatalkMarkup.ToString().Trim(new[] { ' ', '\n', '\r', '\t' }));
        }

        private static void ProcessNodes(StringBuilder output, IEnumerable<HtmlNode> nodes, ProcessModes mode, bool returnHtml)
        {
            foreach (var node in nodes)
                ProcessNode(output, node, mode, returnHtml);
        }

        private static void ProcessNode(StringBuilder output, HtmlNode node, ProcessModes mode, bool returnHtml)
        {
            var lineBreak = returnHtml ? "<br />" : "\r\n"; // (mode == ProcessModes.Quote) ? "\n" : "<br /> ";

            if (node == null || output == null || (mode == ProcessModes.TextOnly && node.Name != "#text"))
                return;

            switch (node.Name)
            {
                // No action needed for these node types
                case "#text":
                    var text = HttpUtility.HtmlDecode(node.InnerHtml);
                    if (mode != ProcessModes.Quote)
                        text = HttpContext.Current.Server.HtmlEncode(text);
                    output.Append(text);
                    return;

                case "table":
                    output.Append(lineBreak);
                    output.Append(" { Table Removed } ");
                    output.Append(lineBreak);
                    return;

                case "script":
                    return;

                case "ol":
                case "ul":

                    if(mode != ProcessModes.Normal)
                        return;

                    output.Append(lineBreak);
                    ProcessNodes(output, node.ChildNodes, mode, returnHtml);
                    output.Append(lineBreak);
                    return;

                case "li":

                    if(mode == ProcessModes.Quote)
                        return; 

                    output.Append("* ");
                    ProcessNodes(output, node.ChildNodes, mode, returnHtml);
                    output.Append(lineBreak);
                    return;

                case "p":
                    ProcessNodes(output, node.ChildNodes, mode, returnHtml);
                    output.Append(lineBreak);
                    //output.Append(lineBreak);
                    return;

                case "b":
                case "strong":

                    if(mode != ProcessModes.Quote)
                    {
                        output.Append("<b>");
                        ProcessNodes(output, node.ChildNodes, mode, returnHtml);
                        output.Append("</b>");
                    }
                    else
                    {
                        output.Append("[b]");
                        ProcessNodes(output, node.ChildNodes, mode, returnHtml);
                        output.Append("[/b]");
                    }

                    return;

                case "i":
                    if(mode != ProcessModes.Quote)
                    {
                        output.Append("<i>");
                        ProcessNodes(output, node.ChildNodes, mode, returnHtml);
                        output.Append("</i>");
                    }
                    else
                    {
                        output.Append("[i]");
                        ProcessNodes(output, node.ChildNodes, mode, returnHtml);
                        output.Append("[/i]");
                    }

                    return;

                case "blockquote":

                    if(mode != ProcessModes.Normal)
                        return;

                    output.Append("[quote]");
                    ProcessNodes(output, node.ChildNodes, mode, returnHtml);
                    output.Append("[/quote]" + lineBreak);
                    return;

                case "br":
                    output.Append(lineBreak);
                    return;


                case "img":

                    var src = node.Attributes["src"];
                    if (src == null || string.IsNullOrWhiteSpace(src.Value))
                        return;

                    var isEmoticon = src.Value.IndexOf("emoticon", 0, StringComparison.InvariantCultureIgnoreCase) >= 0;

                    var url = src.Value;
                    var request = HttpContext.Current.Request;

                    // Make a fully qualifed URL
                    if(!url.StartsWith("/"))
                    {
                        url = string.Format("{0}://{1}{2}", request.Url.Scheme, request.Url.Host, url);
                    }

                    if(mode == ProcessModes.Quote && isEmoticon)
                        return;

                    output.AppendFormat(isEmoticon ? "<img src='{0}' />" : "[img]{0}[/img]", src.Value);

                    return;

                case "a":

                    var href = node.Attributes["href"];
                    if (href == null || string.IsNullOrWhiteSpace(href.Value))
                        return;

                    output.AppendFormat("[url={0}]", href.Value);
                    ProcessNodes(output, node.ChildNodes, ProcessModes.TextOnly, returnHtml); 
                    output.Append("[/url]");

                    return;

            }

            ProcessNodes(output, node.ChildNodes, mode, returnHtml);
        }

        private static string GetAuthorName(SettingsInfo settings, UserInfo user)
        {
            if (user == null || user.UserID <= 0)
                return "Guest";

            switch (settings.UserNameDisplay.ToUpperInvariant())
            {
                case "USERNAME":
                    return user.Username.Trim();
                case "FULLNAME":
                    return (user.FirstName.Trim() + " " + user.LastName.Trim());
                case "FIRSTNAME":
                    return user.FirstName.Trim();
                case "LASTNAME":
                    return user.LastName.Trim();
                default:
                    return user.DisplayName.Trim();
            }

        }

        private static string GetAuthorName(SettingsInfo settings, ForumTopic topic)
        {
            if (topic == null || topic.AuthorId <= 0)
                return "Guest";

            switch (settings.UserNameDisplay.ToUpperInvariant())
            {
                case "USERNAME":
                    return topic.AuthorUserName.Trim();
                case "FULLNAME":
                    return (topic.AuthorFirstName.Trim() + " " + topic.AuthorLastName.Trim());
                case "FIRSTNAME":
                    return topic.AuthorFirstName.Trim();
                case "LASTNAME":
                    return topic.AuthorLastName.Trim();
                default:
                    return topic.AuthorDisplayName.Trim();
            }

        }

        private static string GetAuthorName(SettingsInfo settings, ForumPost post)
        {
            if (post == null || post.AuthorId <= 0)
                return "Guest";

            switch (settings.UserNameDisplay.ToUpperInvariant())
            {
                case "USERNAME":
                    return post.UserName.Trim();
                case "FULLNAME":
                    return (post.FirstName.Trim() + " " + post.LastName.Trim());
                case "FIRSTNAME":
                    return post.FirstName.Trim();
                case "LASTNAME":
                    return post.LastName.Trim();
                default:
                    return post.DisplayName.Trim();
            }

        }

        private static string GetServerDateTime(SettingsInfo settings, DateTime displayDate)
        {
            //Dim newDate As Date 
            string dateString;
            try
            {
                dateString = displayDate.ToString(settings.DateFormatString + " " + settings.TimeFormatString);
                return dateString;
            }
            catch (Exception ex)
            {
                dateString = displayDate.ToString();
                return dateString;
            }
        }

        #endregion

    }
}