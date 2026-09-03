using System.Globalization;
using Accounting_System.Data;
using Accounting_System.Models;
using Accounting_System.Models.AccountsPayable;
using Accounting_System.Models.Reports;
using Accounting_System.Models.ViewModels;
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
    public class PurchaseOrderController : Controller
    {
        private readonly ApplicationDbContext _dbContext;

        private readonly PurchaseOrderRepo _purchaseOrderRepo;

        private readonly UserManager<IdentityUser> _userManager;

        private readonly InventoryRepo _inventoryRepo;

        private readonly GeneralRepo _generalRepo;

        public PurchaseOrderController(ApplicationDbContext dbContext, PurchaseOrderRepo purchaseOrderRepo,
            UserManager<IdentityUser> userManager,
            InventoryRepo inventoryRepo,
            GeneralRepo generalRepo)
        {
            _dbContext = dbContext;
            _purchaseOrderRepo = purchaseOrderRepo;
            _userManager = userManager;
            _inventoryRepo = inventoryRepo;
            _generalRepo = generalRepo;
        }

        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> GetPurchaseOrders([FromForm] DataTablesParameters parameters, CancellationToken cancellationToken)
        {
            try
            {
                var purchaseOrders = await _purchaseOrderRepo.GetPurchaseOrderAsync(cancellationToken);
                // Search filter
                if (!string.IsNullOrEmpty(parameters.Search.Value))
                {
                    var searchValue = parameters.Search.Value.ToLower();
                    purchaseOrders = purchaseOrders
                        .Where(po =>
                            po.PurchaseOrderNo!.ToLower().Contains(searchValue) ||
                            po.Date.ToString("MMM dd, yyyy").ToLower().Contains(searchValue) ||
                            po.Supplier!.SupplierName.ToLower().Contains(searchValue) ||
                            po.Product!.ProductName.ToLower().Contains(searchValue) ||
                            po.Amount.ToString(CultureInfo.InvariantCulture).Contains(searchValue) ||
                            po.Quantity.ToString(CultureInfo.InvariantCulture).Contains(searchValue) ||
                            po.Remarks.ToLower().Contains(searchValue) ||
                            po.CreatedBy!.ToLower().Contains(searchValue)
                            )
                        .ToList();
                }
                // Sorting
                if (parameters.Order != null && parameters.Order.Count > 0)
                {
                    var orderColumn = parameters.Order[0];
                    var columnName = parameters.Columns[orderColumn.Column].Data;
                    var sortDirection = orderColumn.Dir.ToLower() == "asc" ? "ascending" : "descending";
                    purchaseOrders = purchaseOrders
                        .AsQueryable()
                        .OrderBy($"{columnName} {sortDirection}")
                        .ToList();
                }
                var totalRecords = purchaseOrders.Count();
                var pagedData = purchaseOrders
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
        public async Task<IActionResult> GetAllPurchaseOrderIds(CancellationToken cancellationToken)
        {
            var purchaseOrderIds = await _dbContext.PurchaseOrders
                                     .Select(po => po.PurchaseOrderId) // Assuming Id is the primary key
                                     .ToListAsync(cancellationToken);
            return Json(purchaseOrderIds);
        }

        [HttpGet]
        public async Task<IActionResult> Create(CancellationToken cancellationToken)
        {
            var viewModel = new PurchaseOrder
            {
                Suppliers = await _dbContext.Suppliers
                    .Select(s => new SelectListItem
                    {
                        Value = s.SupplierId.ToString(),
                        Text = s.SupplierName
                    })
                    .ToListAsync(cancellationToken),
                Products = await _dbContext.Products
                    .Select(s => new SelectListItem
                    {
                        Value = s.ProductId.ToString(),
                        Text = s.ProductName
                    })
                    .ToListAsync(cancellationToken)
            };

            return View(viewModel);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(PurchaseOrder model, CancellationToken cancellationToken)
        {
            model.Suppliers = await _dbContext.Suppliers
                .Select(s => new SelectListItem
                {
                    Value = s.SupplierId.ToString(),
                    Text = s.SupplierName
                })
                .ToListAsync(cancellationToken);

            model.Products = await _dbContext.Products
                .Select(s => new SelectListItem
                {
                    Value = s.ProductId.ToString(),
                    Text = s.ProductName
                })
                .ToListAsync(cancellationToken);

            if (ModelState.IsValid)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
                try
                {
                    var generatedPo = await _purchaseOrderRepo.GeneratePONo(cancellationToken);
                    var getLastNumber = long.Parse(generatedPo.Substring(2));

                    if (getLastNumber > 9999999999)
                    {
                        TempData["error"] = "You reach the maximum Series Number";
                        return View(model);
                    }
                    var totalRemainingSeries = 9999999999 - getLastNumber;
                    if (getLastNumber >= 9999999899)
                    {
                        TempData["warning"] = $"Purchase Order created successfully, Warning {totalRemainingSeries} series number remaining";
                    }
                    else
                    {
                        TempData["success"] = "Purchase Order created successfully";
                    }

                    model.PurchaseOrderNo = generatedPo;
                    model.CreatedBy = createdBy;
                    model.Amount = model.Quantity * model.Price;
                    model.SupplierNo = await _purchaseOrderRepo.GetSupplierNoAsync(model.SupplierId, cancellationToken);
                    model.ProductNo = await _purchaseOrderRepo.GetProductNoAsync(model.ProductId, cancellationToken);

                    await _dbContext.AddAsync(model, cancellationToken);

                    #region --Audit Trail Recording

                    if (model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId == 0)
                    {
                        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                        AuditTrail auditTrailBook = new(createdBy, $"Create new purchase order# {model.PurchaseOrderNo}", "Purchase Order", ipAddress!);
                        await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                    }

                    #endregion --Audit Trail Recording

                    await _dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return RedirectToAction(nameof(Index));
                }
                catch (Exception ex)
                {
                 await transaction.RollbackAsync(cancellationToken);
                 TempData["error"] = ex.Message;
                 return RedirectToAction(nameof(Index));
                }
            }
            else
            {
                ModelState.AddModelError("", "The information you submitted is not valid!");
                return View(model);
            }
        }

        [HttpGet]
        public async Task<IActionResult> Edit(int? id, CancellationToken cancellationToken)
        {
            if (id == null || !_dbContext.PurchaseOrders.Any())
            {
                return NotFound();
            }

            var purchaseOrder = await _purchaseOrderRepo.FindPurchaseOrder(id, cancellationToken);

            purchaseOrder.Suppliers = await _dbContext.Suppliers
                .Select(s => new SelectListItem
                {
                    Value = s.SupplierId.ToString(),
                    Text = s.SupplierName
                })
                .ToListAsync(cancellationToken);

            purchaseOrder.Products = await _dbContext.Products
                .Select(s => new SelectListItem
                {
                    Value = s.ProductId.ToString(),
                    Text = s.ProductName
                })
                .ToListAsync(cancellationToken);

            ViewBag.PurchaseOrders = purchaseOrder.Quantity;

            return View(purchaseOrder);
        }

        [HttpPost]
        public async Task<IActionResult> Edit(PurchaseOrder model, CancellationToken cancellationToken)
        {
            var existingModel = await _dbContext.PurchaseOrders.FirstOrDefaultAsync(x => x.PurchaseOrderId == model.PurchaseOrderId, cancellationToken);
            if (ModelState.IsValid)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
                try
                {
                    if (existingModel == null)
                    {
                        return NotFound();
                    }

                    existingModel.Date = model.Date;
                    existingModel.SupplierId = model.SupplierId;
                    existingModel.ProductId = model.ProductId;
                    existingModel.Quantity = model.Quantity;
                    existingModel.Price = model.Price;
                    existingModel.Amount = model.Quantity * model.Price;
                    existingModel.Remarks = model.Remarks;
                    existingModel.Terms = model.Terms;
                    existingModel.SupplierNo = await _purchaseOrderRepo.GetSupplierNoAsync(model.SupplierId, cancellationToken);
                    existingModel.ProductNo = await _purchaseOrderRepo.GetProductNoAsync(model.ProductId, cancellationToken);

                    if (_dbContext.ChangeTracker.HasChanges())
                    {
                        #region --Audit Trail Recording

                        if (existingModel.OriginalSeriesNumber.IsNullOrEmpty() && existingModel.OriginalDocumentId == 0)
                        {
                            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                            AuditTrail auditTrailBook = new(createdBy, $"Edit purchase order# {existingModel.PurchaseOrderNo}", "Purchase Order", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        TempData["success"] = "Purchase Order updated successfully";
                        return RedirectToAction(nameof(Index));
                    }
                    else
                    {
                        throw new InvalidOperationException("No data changes!");
                    }

                }
                catch (Exception ex)
                {
                 await transaction.RollbackAsync(cancellationToken);
                 existingModel!.Suppliers = await _dbContext.Suppliers
                     .Select(s => new SelectListItem
                     {
                         Value = s.SupplierId.ToString(),
                         Text = s.SupplierName
                     })
                     .ToListAsync(cancellationToken);

                 existingModel.Products = await _dbContext.Products
                     .Select(s => new SelectListItem
                     {
                         Value = s.ProductId.ToString(),
                         Text = s.ProductName
                     })
                     .ToListAsync(cancellationToken);
                 TempData["error"] = ex.Message;
                 return View(existingModel);
                }
            }

            existingModel!.Suppliers = await _dbContext.Suppliers
                .Select(s => new SelectListItem
                {
                    Value = s.SupplierId.ToString(),
                    Text = s.SupplierName
                })
                .ToListAsync(cancellationToken);

            existingModel.Products = await _dbContext.Products
                .Select(s => new SelectListItem
                {
                    Value = s.ProductId.ToString(),
                    Text = s.ProductName
                })
                .ToListAsync(cancellationToken);
            return View(existingModel);
        }

        [HttpGet]
        public async Task<IActionResult> Print(int? id, CancellationToken cancellationToken)
        {
            if (id == null)
            {
                return NotFound();
            }

            var purchaseOrder = await _purchaseOrderRepo
                .FindPurchaseOrder(id, cancellationToken);

            return View(purchaseOrder);
        }

        public async Task<IActionResult> Printed(int id, CancellationToken cancellationToken)
        {
            var po = await _dbContext.PurchaseOrders.FirstOrDefaultAsync(x => x.PurchaseOrderId == id, cancellationToken);
            var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
            if (po != null && !po.IsPrinted)
            {

                #region --Audit Trail Recording

                if (po.OriginalSeriesNumber.IsNullOrEmpty() && po.OriginalDocumentId == 0)
                {
                    var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                    AuditTrail auditTrailBook = new(createdBy, $"Printed original copy of po# {po.PurchaseOrderNo}", "Purchase Order", ipAddress!);
                    await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                }

                #endregion --Audit Trail Recording

                po.IsPrinted = true;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            return RedirectToAction(nameof(Print), new { id });
        }

        public async Task<IActionResult> Post(int id, CancellationToken cancellationToken)
        {
            var model = await _dbContext.PurchaseOrders.FirstOrDefaultAsync(x => x.PurchaseOrderId == id, cancellationToken);
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var createdBy = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.PostedBy : await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
            var date = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.PostedDate : DateTime.Now;
            try
            {
                if (model != null)
                {
                    if (!model.IsPosted)
                    {
                        model.IsPosted = true;
                        model.PostedBy = createdBy;
                        model.PostedDate = date;

                        #region --Audit Trail Recording

                        if (model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId == 0)
                        {
                            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                            AuditTrail auditTrailBook = new(createdBy, $"Posted purchase order# {model.PurchaseOrderNo}", "Purchase Order", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        TempData["success"] = "Purchase Order has been Posted.";
                    }
                    return RedirectToAction(nameof(Print), new { id });
                }
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                TempData["error"] = ex.Message;
                return RedirectToAction(nameof(Index));
            }

            return NotFound();
        }

        public async Task<IActionResult> Void(int id, CancellationToken cancellationToken)
        {
            var model = await _dbContext.PurchaseOrders.FirstOrDefaultAsync(x => x.PurchaseOrderId == id, cancellationToken);
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var createdBy = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.VoidedBy : await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
            var date = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.VoidedDate : DateTime.Now;
            try
            {
                if (model != null)
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

                        #region --Audit Trail Recording

                        if (model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId == 0)
                        {
                            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                            AuditTrail auditTrailBook = new(createdBy, $"Voided purchase order# {model.PurchaseOrderNo}", "Purchase Order", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        TempData["success"] = "Purchase Order has been Voided.";
                    }
                    return RedirectToAction(nameof(Index));
                }
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                TempData["error"] = ex.Message;
                return RedirectToAction(nameof(Index));
            }

            return NotFound();
        }

        public async Task<IActionResult> Cancel(int id, string cancellationRemarks, CancellationToken cancellationToken)
        {
            var model = await _dbContext.PurchaseOrders.FirstOrDefaultAsync(x => x.PurchaseOrderId == id, cancellationToken);
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var createdBy = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.CanceledBy : await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
            var date = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.CanceledDate : DateTime.Now;
            try
            {
                if (model != null)
                {
                    if (!model.IsCanceled)
                    {
                        model.IsCanceled = true;
                        model.CanceledBy = createdBy;
                        model.CanceledDate = date;
                        model.CancellationRemarks = cancellationRemarks;

                        #region --Audit Trail Recording

                        if (model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId == 0)
                        {
                            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                            AuditTrail auditTrailBook = new(createdBy, $"Cancelled purchase order# {model.PurchaseOrderNo}", "Purchase Order", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        TempData["success"] = "Purchase Order has been Cancelled.";
                    }
                    return RedirectToAction(nameof(Index));
                }
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                TempData["error"] = ex.Message;
                return RedirectToAction(nameof(Index));
            }

            return NotFound();
        }

        [HttpGet]
        public async Task<IActionResult> ChangePrice(CancellationToken cancellationToken)
        {
            PurchaseChangePriceViewModel po = new()
            {
                PO = await _dbContext.PurchaseOrders
                    .Where(po => po.FinalPrice == 0 || po.FinalPrice == null && po.IsPosted && po.QuantityReceived != 0)
                    .Select(s => new SelectListItem
                    {
                        Value = s.PurchaseOrderId.ToString(),
                        Text = s.PurchaseOrderNo
                    })
                    .ToListAsync(cancellationToken)
            };

            return View(po);
        }

        [HttpPost]
        public async Task<IActionResult> ChangePrice(PurchaseChangePriceViewModel model, CancellationToken cancellationToken)
        {
            if (ModelState.IsValid)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                try
                {
                    var existingModel = await _dbContext.PurchaseOrders.FirstOrDefaultAsync(x => x.PurchaseOrderId == model.POId, cancellationToken);

                    existingModel!.FinalPrice = model.FinalPrice;

                    #region--Inventory Recording

                    await _inventoryRepo.ChangePriceToInventoryAsync(model, User, cancellationToken);

                    #endregion

                    #region --Audit Trail Recording

                    if (existingModel.OriginalSeriesNumber.IsNullOrEmpty() && existingModel.OriginalDocumentId == 0)
                    {
                        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                        AuditTrail auditTrailBook = new(existingModel.CreatedBy!, $"Change price, purchase order# {existingModel.PurchaseOrderNo}", "Purchase Order", ipAddress!);
                        await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                    }

                    #endregion --Audit Trail Recording

                    await _dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    TempData["success"] = "Change Price updated successfully";
                    return RedirectToAction(nameof(Index));
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    model.PO = await _dbContext.PurchaseOrders
                        .Where(po => po.FinalPrice == 0 || po.FinalPrice == null && po.IsPosted && po.QuantityReceived != 0)
                        .Select(s => new SelectListItem
                        {
                            Value = s.PurchaseOrderId.ToString(),
                            Text = s.PurchaseOrderNo
                        })
                        .ToListAsync(cancellationToken);

                    TempData["error"] = ex.Message;
                    return View(model);
                }

            }
            model.PO = await _dbContext.PurchaseOrders
                .Where(po => po.FinalPrice == 0 || po.FinalPrice == null && po.IsPosted)
                .Select(s => new SelectListItem
                {
                    Value = s.PurchaseOrderId.ToString(),
                    Text = s.PurchaseOrderNo
                })
                .ToListAsync(cancellationToken);

            TempData["error"] = "The information provided was invalid.";
            return View(nameof(ChangePrice));
        }

        [HttpGet]
        public async Task<IActionResult> ClosePO(int id, CancellationToken cancellationToken)
        {
            var purchaseOrder = await _dbContext.PurchaseOrders.FirstOrDefaultAsync(x => x.PurchaseOrderId == id, cancellationToken);

            if (purchaseOrder != null)
            {
                var rrList = await _dbContext.ReceivingReports
                    .Where(rr => rr.PONo == purchaseOrder.PurchaseOrderNo)
                    .ToListAsync(cancellationToken);

                purchaseOrder.RrList = rrList;

                return View(purchaseOrder);
            }

            return NotFound();
        }

        [HttpPost]
        public async Task<IActionResult> ClosePO(PurchaseOrder model, CancellationToken cancellationToken)
        {
            var purchaseOrder = await _dbContext.PurchaseOrders.FirstOrDefaultAsync(x => x.PurchaseOrderId == model.PurchaseOrderId, cancellationToken);
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
            try
            {
                if (purchaseOrder != null)
                {
                    if (!purchaseOrder.IsClosed)
                    {
                        purchaseOrder.IsClosed = true;

                        #region --Audit Trail Recording

                        if (model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId == 0)
                        {
                            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                            AuditTrail auditTrailBook = new(createdBy, $"Closed purchase order# {model.PurchaseOrderNo}", "Purchase Order", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        TempData["success"] = "Purchase Order has been Closed.";
                    }
                    return RedirectToAction(nameof(Index));
                }
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                TempData["error"] = ex.Message;
                return RedirectToAction(nameof(Index));
            }

            return NotFound();
        }
    }
}
