﻿using System;

namespace DotNetNuke.Modules.ActiveForumsTapatalk.Classes
{
    public class ListForum
    {
        public int ForumId { get; set; }
        public string ForumName { get; set; }
        public DateTime LastPostDate { get; set; }
        public DateTime LastAccessDate { get; set; }
    }
}