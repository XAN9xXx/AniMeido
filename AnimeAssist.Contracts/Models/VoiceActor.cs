using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnimeAssist.Contracts.Models
{
    public record VoiceActor(string Name, string Role, string CoverURL);
        /*
        * Name: 声优的姓名
        * Role: 声优在动漫中的角色
        * CoverURL: 声优的头像图片URL
        */
}
