using System.Globalization;
using Accounting_System.Data;
using Accounting_System.Models;
using Accounting_System.Models.AccountsPayable;
using Accounting_System.Models.Reports;
using Accounting_System.Repository;
using Accounting_System.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using OfficeOpenXml;
using System.Linq.Dynamic.Core;
using Microsoft.IdentityModel.Tokens;

namespace Accounting_System.Controllers
{
    [Authorize]
    public class ReceivingReportController : Controller
    {
        private readonly ApplicationDbContext _dbContext;

        private readonly UserManager<IdentityUser> _userManager;

        private readonly ReceivingReportRepo _receivingReportRepo;

        private readonly PurchaseOrderRepo _purchaseOrderRepo;

        private readonly GeneralRepo _generalRepo;

        private readonly InventoryRepo _inventoryRepo;

        public ReceivingReportController(ApplicationDbContext dbContext, UserManager<IdentityUser> userManager, ReceivingReportRepo receivingReportRepo, GeneralRepo generalRepo, InventoryRepo inventoryRepo, PurchaseOrderRepo purchaseOrderRepo)
        {
            _dbContext = dbContext;
            _userManager = userManager;
            _receivingReportRepo = receivingReportRepo;
            _purchaseOrderRepo = purchaseOrderRepo;
            _generalRepo = generalRepo;
            _inventoryRepo = inventoryRepo;
        }

        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> GetReceivingReports([FromForm] DataTablesParameters parameters, CancellationToken cancellationToken)
        {
            try
            {
                var receivingReports = await _receivingReportRepo.GetReceivingReportsAsync(cancellationToken);
                // Search filter
                if (!string.IsNullOrEmpty(parameters.Search.Value))
                {
                    var searchValue = parameters.Search.Value.ToLower();
                    receivingReports = receivingReports
                        .Where(rr =>
                            rr.ReceivingReportNo?.ToLower().Contains(searchValue) == true ||
                            rr.Date.ToString("MMM dd, yyyy").ToLower().Contains(searchValue) ||
                            rr.PONo?.ToLower().Contains(searchValue) == true ||
                            rr.QuantityDelivered.ToString(CultureInfo.InvariantCulture).Contains(searchValue) ||
                            rr.QuantityReceived.ToString(CultureInfo.InvariantCulture).Contains(searchValue) ||
                            rr.Amount.ToString(CultureInfo.InvariantCulture).Contains(searchValue) ||
                            rr.Remarks.ToString().ToLower().Contains(searchValue) ||
                            rr.CreatedBy!.ToLower().Contains(searchValue)
                            )
                        .ToList();
                }
                // Sorting
                if (parameters.Order != null && parameters.Order.Count > 0)
                {
                    var orderColumn = parameters.Order[0];
                    var columnName = parameters.Columns[orderColumn.Column].Data;
                    var sortDirection = orderColumn.Dir.ToLower() == "asc" ? "ascending" : "descending";
                    receivingReports = receivingReports
                        .AsQueryable()
                        .OrderBy($"{columnName} {sortDirection}")
                        .ToList();
                }
                var totalRecords = receivingReports.Count();
                var pagedData = receivingReports
                    .Skip(parameters.Start)
                    .Take(parameters.Length)
                    .ToList();
                return Json(new
                {
                    draw = parameters.Draw,
                    recordsTotal = totalRecords,
                    recordsFiltered = totalRecords,
                    data = pagedData
                });
            }
            catch (Exception ex)
            {
                TempData["error"] = ex.Message;
                return RedirectToAction(nameof(Index));
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetAllReceivingReportIds(CancellationToken cancellationToken)
        {
            var receivingReportIds = await _dbContext.ReceivingReports
                                     .Select(rr => rr.ReceivingReportId) // Assuming Id is the primary key
                                     .ToListAsync(cancellationToken);
            return Json(receivingReportIds);
        }

        [HttpGet]
        public async Task<IActionResult> Create(CancellationToken cancellationToken)
        {
            var viewModel = new ReceivingReport
            {
                PurchaseOrders = await _dbContext.PurchaseOrders
                    .Where(po => !po.IsReceived && po.IsPosted && !po.IsClosed)
                    .Select(po => new SelectListItem
                    {
                        Value = po.PurchaseOrderId.ToString(),
                        Text = po.PurchaseOrderNo
                    })
                    .ToListAsync(cancellationToken)
            };

            return View(viewModel);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(ReceivingReport model, CancellationToken cancellationToken)
        {
            model.PurchaseOrders = await _dbContext.PurchaseOrders
                .Where(po => !po.IsReceived && po.IsPosted)
                .Select(po => new SelectListItem
                {
                    Value = po.PurchaseOrderId.ToString(),
                    Text = po.PurchaseOrderNo
                })
                .ToListAsync(cancellationToken);
            var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
            if (ModelState.IsValid)
            {
                #region --Retrieve PO

                var existingPo = await _dbContext
                            .PurchaseOrders
                            .Include(po => po.Supplier)
                            .Include(po => po.Product)
                            .FirstOrDefaultAsync(po => po.PurchaseOrderId == model.POId, cancellationToken);

                #endregion --Retrieve PO

                var totalAmountRr = existingPo!.Quantity - existingPo.QuantityReceived;

                if (model.QuantityDelivered > totalAmountRr)
                {
                    TempData["error"] = "Input is exceed to remaining quantity delivered";
                    return View(model);
                }

                #region --Validating Series

                var generatedRr = await _receivingReportRepo.GenerateRRNo(cancellationToken);
                var getLastNumber = long.Parse(generatedRr.Substring(2));

                if (getLastNumber > 9999999999)
                {
                    TempData["error"] = "You reach the maximum Series Number";
                    return View(model);
                }
                var totalRemainingSeries = 9999999999 - getLastNumber;
                if (getLastNumber >= 9999999899)
                {
                    TempData["warning"] = $"Receiving Report created successfully, Warning {totalRemainingSeries} series number remaining";
                }
                else
                {
                    TempData["success"] = "Receiving Report created successfully";
                }

                #endregion --Validating Series

                model.ReceivingReportNo = generatedRr;
                model.CreatedBy = createdBy;
                model.GainOrLoss = model.QuantityReceived - model.QuantityDelivered;
                model.PONo = await _receivingReportRepo.GetPONoAsync(model.POId, cancellationToken);
                model.DueDate = await _receivingReportRepo.ComputeDueDateAsync(model.POId, model.Date, cancellationToken);

                model.Amount = model.QuantityReceived * existingPo.Price;

                #region --Audit Trail Recording

                if (model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId == 0)
                {
                    var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                    AuditTrail auditTrailBook = new(createdBy, $"Create new receiving report# {model.ReceivingReportNo}", "Receiving Report", ipAddress!);
                    await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                }

                #endregion --Audit Trail Recording

                await _dbContext.AddAsync(model, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);

                return RedirectToAction(nameof(Index));
            }

            ModelState.AddModelError("", "The information you submitted is not valid!");
            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> Edit(int? id, CancellationToken cancellationToken)
        {
            if (id == null || !_dbContext.ReceivingReports.Any())
            {
                return NotFound();
            }

            var receivingReport = await _dbContext.ReceivingReports.FirstOrDefaultAsync(x => x.ReceivingReportId == id, cancellationToken);
            if (receivingReport == null)
            {
                return NotFound();
            }

            receivingReport.PurchaseOrders = await _dbContext.PurchaseOrders
                .Select(s => new SelectListItem
                {
                    Value = s.PurchaseOrderId.ToString(),
                    Text = s.PurchaseOrderNo
                })
                .ToListAsync(cancellationToken);

            return View(receivingReport);
        }

        [HttpPost]
        public async Task<IActionResult> Edit(ReceivingReport model, CancellationToken cancellationToken)
        {
            var existingModel = await _dbContext.ReceivingReports.FirstOrDefaultAsync(x => x.ReceivingReportId == model.ReceivingReportId, cancellationToken);

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
            try
            {
                if (ModelState.IsValid)
                {
                    if (existingModel == null)
                    {
                        return NotFound();
                    }

                    #region --Retrieve PO

                    var po = await _dbContext
                                .PurchaseOrders
                                .Include(po => po.Supplier)
                                .Include(po => po.Product)
                                .FirstOrDefaultAsync(po => po.PurchaseOrderId == model.POId, cancellationToken);

                    #endregion --Retrieve PO

                    var totalAmountRr = po!.Quantity - po.QuantityReceived;

                    if (model.QuantityDelivered > totalAmountRr && !existingModel.IsPosted)
                    {
                        TempData["error"] = "Input is exceed to remaining quantity delivered";
                        existingModel.PurchaseOrders = await _dbContext.PurchaseOrders
                            .Select(s => new SelectListItem
                            {
                                Value = s.PurchaseOrderId.ToString(),
                                Text = s.PurchaseOrderNo
                            })
                            .ToListAsync(cancellationToken);
                        return View(existingModel);
                    }

                    existingModel.Date = model.Date;
                    existingModel.POId = model.POId;
                    existingModel.PONo = await _receivingReportRepo.GetPONoAsync(model.POId, cancellationToken);
                    existingModel.DueDate = await _receivingReportRepo.ComputeDueDateAsync(model.POId, model.Date, cancellationToken);
                    existingModel.SupplierInvoiceNumber = model.SupplierInvoiceNumber;
                    existingModel.SupplierInvoiceDate = model.SupplierInvoiceDate;
                    existingModel.TruckOrVessels = model.TruckOrVessels;
                    existingModel.QuantityDelivered = model.QuantityDelivered;
                    existingModel.QuantityReceived = model.QuantityReceived;
                    existingModel.GainOrLoss = model.QuantityReceived - model.QuantityDelivered;
                    existingModel.OtherRef = model.OtherRef;
                    existingModel.Remarks = model.Remarks;
                    existingModel.ReceivedDate = model.ReceivedDate;
                    existingModel.Amount = model.QuantityReceived * po.Price;

                    if (_dbContext.ChangeTracker.HasChanges())
                    {
                        #region --Audit Trail Recording

                        if (existingModel.OriginalSeriesNumber.IsNullOrEmpty() && existingModel.OriginalDocumentId == 0)
                        {
                            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                            AuditTrail auditTrailBook = new(createdBy, $"Edited receiving report# {existingModel.ReceivingReportNo}", "Receiving Report", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        TempData["success"] = "Receiving Report updated successfully";
                        await transaction.CommitAsync(cancellationToken);
                        return RedirectToAction(nameof(Index));
                    }
                    else
                    {
                        throw new InvalidOperationException("No data changes!");
                    }
                }
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                existingModel!.PurchaseOrders = await _dbContext.PurchaseOrders
                    .Select(s => new SelectListItem
                    {
                        Value = s.PurchaseOrderId.ToString(),
                        Text = s.PurchaseOrderNo
                    })
                    .ToListAsync(cancellationToken);
                TempData["error"] = ex.Message;
                return View(existingModel);
            }

            existingModel!.PurchaseOrders = await _dbContext.PurchaseOrders
                .Select(s => new SelectListItem
                {
                    Value = s.PurchaseOrderId.ToString(),
                    Text = s.PurchaseOrderNo
                })
                .ToListAsync(cancellationToken);
            return View(existingModel);
        }

        [HttpGet]
        public async Task<IActionResult> Print(int id, CancellationToken cancellationToken)
        {
            if (id == 0 || !_dbContext.ReceivingReports.Any())
            {
                return NotFound();
            }

            var receivingReport = await _receivingReportRepo.FindRR(id, cancellationToken);

            return View(receivingReport);
        }

        public async Task<IActionResult> Printed(int id, CancellationToken cancellationToken)
        {
            var rr = await _dbContext.ReceivingReports.FirstOrDefaultAsync(x => x.ReceivingReportId == id, cancellationToken);
            var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
            if (rr != null && !rr.IsPrinted)
            {

                #region --Audit Trail Recording

                if (rr.OriginalSeriesNumber.IsNullOrEmpty() && rr.OriginalDocumentId == 0)
                {
                    var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                    AuditTrail auditTrailBook = new(createdBy, $"Printed original copy of rr# {rr.ReceivingReportNo}", "Receiving Report", ipAddress!);
                    await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                }

                #endregion --Audit Trail Recording

                rr.IsPrinted = true;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            return RedirectToAction(nameof(Print), new { id });
        }

        public async Task<IActionResult> Post(int id, CancellationToken cancellationToken)
        {
            var model = await _receivingReportRepo.FindRR(id, cancellationToken);

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var createdBy = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.PostedBy : await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
            var date = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.PostedDate : DateTime.Now;
            try
            {
                if (model.ReceivedDate == null)
                {
                    TempData["error"] = "Please indicate the received date.";
                    return RedirectToAction(nameof(Index));
                }

                if (!model.IsPosted)
                {
                    model.IsPosted = true;
                    model.PostedBy = createdBy;
                    model.PostedDate = date;

                    await _receivingReportRepo.PostAsync(model, User, cancellationToken);

                    #region --Audit Trail Recording

                    if (model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId == 0)
                    {
                        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                        AuditTrail auditTrailBook = new(createdBy, $"Posted rr# {model.ReceivingReportNo}", "Receiving Report", ipAddress!);
                        await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                    }

                    #endregion --Audit Trail Recording

                    await _dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    TempData["success"] = "Receiving Report has been Posted.";
                    return RedirectToAction(nameof(Print), new { id });
                }
                else
                {
                    return RedirectToAction(nameof(Print), new { id });
                }
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                TempData["error"] = ex.Message;
                return RedirectToAction(nameof(Print), new { id });
            }
        }

        public async Task<IActionResult> Void(int id, CancellationToken cancellationToken)
        {
            var model = await _dbContext.ReceivingReports
                .FirstOrDefaultAsync(x => x.ReceivingReportId == id, cancellationToken);

            var existingInventory = await _dbContext.Inventories
                .Include(i => i.Product)
                .FirstOrDefaultAsync(i => i.Reference == model!.ReceivingReportNo, cancellationToken: cancellationToken);

            if (model != null && existingInventory != null)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var createdBy = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.VoidedBy : await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
                var date = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.VoidedDate : DateTime.Now;
                try
                {
                    if (!model.IsVoided)
                    {
                        if (model.IsPosted)
                        {
                            model.IsPosted = false;
                        }
                        model.IsVoided = true;
                        model.VoidedBy = createdBy;
                        model.VoidedDate = date;

                        await _generalRepo.RemoveRecords<PurchaseJournalBook>(pb => pb.DocumentNo == model.ReceivingReportNo, cancellationToken);
                        await _generalRepo.RemoveRecords<GeneralLedgerBook>(gl => gl.Reference == model.ReceivingReportNo, cancellationToken);
                        await _inventoryRepo.VoidInventory(existingInventory, cancellationToken);
                        await _receivingReportRepo.RemoveQuantityReceived(model.POId, model.QuantityReceived, cancellationToken);
                        model.QuantityReceived = 0;

                        #region --Audit Trail Recording

                        if (model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId == 0)
                        {
                            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                            AuditTrail auditTrailBook = new(createdBy, $"Voided rr# {model.ReceivingReportNo}", "Receiving Report", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        TempData["success"] = "Receiving Report has been Voided.";
                    }
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    TempData["error"] = ex.Message;
                    return RedirectToAction(nameof(Index));
                }
                return RedirectToAction(nameof(Index));
            }

            return NotFound();
        }

        public async Task<IActionResult> Cancel(int id, string cancellationRemarks, CancellationToken cancellationToken)
        {
            var model = await _dbContext.ReceivingReports.FirstOrDefaultAsync(x => x.ReceivingReportId == id, cancellationToken);

            if (model != null)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var createdBy = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.CanceledBy : await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
                var date = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.CanceledDate : DateTime.Now;
                try
                {
                    if (!model.IsCanceled)
                    {
                        model.IsCanceled = true;
                        model.CanceledBy = createdBy;
                        model.CanceledDate = date;
                        model.CanceledQuantity = model.QuantityDelivered < model.QuantityReceived ? model.QuantityDelivered : model.QuantityReceived;
                        model.QuantityDelivered = 0;
                        model.QuantityReceived = 0;
                        model.CancellationRemarks = cancellationRemarks;

                        #region --Audit Trail Recording

                        if (model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId == 0)
                        {
                            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                            AuditTrail auditTrailBook = new(createdBy, $"Cancelled rr# {model.ReceivingReportNo}", "Receiving Report", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        TempData["success"] = "Receiving Report has been Cancelled.";
                    }
                    return RedirectToAction(nameof(Index));
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    TempData["error"] = ex.Message;
                    return RedirectToAction(nameof(Index));
                }
            }

            return NotFound();
        }

        [HttpGet]
        public async Task<IActionResult> GetLiquidations(int id, CancellationToken cancellationToken)
        {
            var po = await _receivingReportRepo.GetPurchaseOrderAsync(id, cancellationToken);

            var rrPostedOnly = await _dbContext
                .ReceivingReports
                .Where(rr => rr.PONo == po.PurchaseOrderNo && rr.IsPosted)
                .ToListAsync(cancellationToken);

            var rr = await _dbContext
                .ReceivingReports
                .Where(rr => rr.PONo == po.PurchaseOrderNo)
                .ToListAsync(cancellationToken);

            var rrNotPosted = await _dbContext
                .ReceivingReports
                .Where(x => x.PONo == po.PurchaseOrderNo && !x.IsPosted && !x.IsCanceled)
                .ToListAsync(cancellationToken);

            var rrCanceled = await _dbContext
                .ReceivingReports
                .Where(x => x.PONo == po.PurchaseOrderNo && x.IsCanceled)
                .ToListAsync(cancellationToken);

            if (po.PurchaseOrderId != 0)
            {
                return Json(new
                {
                    poNo = po.PurchaseOrderNo,
                    poQuantity = po.Quantity.ToString("N2"),
                    rrList = rr,
                    rrListPostedOnly = rrPostedOnly,
                    rrListNotPosted = rrNotPosted,
                    rrListCanceled = rrCanceled
                });
            }

            return Json(null);
        }
    }
}
