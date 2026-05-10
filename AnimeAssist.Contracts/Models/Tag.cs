using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnimeAssist.Contracts.Models
{
    public record Tag(int ID, string Name, string? Category);
        /*
        * ID: 标签的唯一标识符
        * Name: 标签的名称
        * Category: 标签的分类
        */

}
