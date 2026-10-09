using System;
using System.Collections.Generic;
using System.Text;

namespace POS.Shared.Models
{
    public class UomDto
    {
        public int UomId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Abbreviation { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
    }
}
