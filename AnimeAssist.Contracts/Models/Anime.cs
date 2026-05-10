using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnimeAssist.Contracts.Models
{
    public record Anime(int ID, string Title, string Studio, List<VoiceActor> CVs, DateOnly AirDate, string CoverURL, string Description, List<Tag> Tags);
        /*
         * ID: 动漫的唯一标识符
         * Title: 动漫的标题
         * Studio: 动漫的制作公司
         * CVs: 参与配音的声优列表
         * AirDate: 动漫的首播日期
         * CoverURL: 动漫的封面图片URL
         * Description: 动漫的简介
         * Tags: 与动漫相关的标签列表，例如类型、主题等
         */

}
