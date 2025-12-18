using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Accounting_System.Models
{
    public class BaseEntity
    {
        [Display(Name = "Created By")]
        [StringLength(50)]
        public string? CreatedBy { get; set; }

        [Display(Name = "Created Date")]
        [Column(TypeName = "timestamp without time zone")]
        public DateTime CreatedDate { get; set; } = DateTime.Now;

        public bool IsPrinted { get; set; }

        public bool IsCanceled { get; set; }

        public bool IsVoided { get; set; }

        public bool IsPosted { get; set; }

        [StringLength(50)]
        public string? CanceledBy { get; set; }

        [Column(TypeName = "timestamp without time zone")]
        public DateTime? CanceledDate { get; set; }

        [StringLength(50)]
        public string? VoidedBy { get; set; }

        [Column(TypeName = "timestamp without time zone")]
        public DateTime? VoidedDate { get; set; }

        [StringLength(50)]
        public string? PostedBy { get; set; }

        [Column(TypeName = "timestamp without time zone")]
        public DateTime? PostedDate { get; set; }

        public string? CancellationRemarks { get; set; }

        public string? OriginalSeriesNumber { get; set; }

        public int OriginalDocumentId { get; set; }

        [Display(Name = "Edited By")]
        [Column(TypeName = "varchar(50)")]
        public string EditedBy { get; set; } = string.Empty;

        [Display(Name = "Edited Date")]
        [Column(TypeName = "timestamp without time zone")]
        public DateTime? EditedDate { get; set; }
    }
}
