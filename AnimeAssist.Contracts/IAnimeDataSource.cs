using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AnimeAssist.Contracts.Models;

namespace AnimeAssist.Contracts
{
    public interface IAnimeDataSource
    {
        Task<List<Anime>> GetSeasonalAnimeAsync(int year, string season);
        Task<Anime?> GetAnimeDetailsAsync(int animeID);
        Task<List<Studio>> GetStudioAsync(int animeID);
        Task<List<Tag>> GetTagsAsync(int animeID);
    }
        /*
         * GetSeasonalAnimeAsync: 获取指定年份和季度的动漫列表
         * GetAnimeDetailsAsync: 获取指定动漫的详细信息
         * GetStudioAsync: 获取指定动漫的制作公司信息
         * GetTagsAsync: 获取指定动漫的标签信息
         */

}
