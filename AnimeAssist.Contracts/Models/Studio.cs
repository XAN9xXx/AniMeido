using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnimeAssist.Contracts.Models
{
    public record Studio(int ID, string Name, string? CoverURL);
        /*
        * ID: 制作公司的唯一标识符
        * Name: 制作公司的名称
        * CoverURL: 制作公司的封面图片URL
        */
}
