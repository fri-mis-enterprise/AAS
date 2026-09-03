using System.Globalization;
using Accounting_System.Data;
using Accounting_System.Models;
using Accounting_System.Models.AccountsReceivable;
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
    public class ServiceInvoiceController : Controller
    {
        private readonly ApplicationDbContext _dbContext;

        private readonly ServiceInvoiceRepo _serviceInvoiceRepo;

        private readonly UserManager<IdentityUser> _userManager;

        private readonly GeneralRepo _generalRepo;

        public ServiceInvoiceController(ApplicationDbContext dbContext, ServiceInvoiceRepo statementOfAccountRepo, UserManager<IdentityUser> userManager, GeneralRepo generalRepo)
        {
            _dbContext = dbContext;
            _serviceInvoiceRepo = statementOfAccountRepo;
            _userManager = userManager;
            _generalRepo = generalRepo;
        }

        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> GetServiceInvoices([FromForm] DataTablesParameters parameters, CancellationToken cancellationToken)
        {
            try
            {
                var serviceInvoices = await _serviceInvoiceRepo.GetServiceInvoicesAsync(cancellationToken);
                // Search filter
                if (!string.IsNullOrEmpty(parameters.Search.Value))
                {
                    var searchValue = parameters.Search.Value.ToLower();
                    serviceInvoices = serviceInvoices
                        .Where(sv =>
                            sv.ServiceInvoiceNo!.ToLower().Contains(searchValue) ||
                            sv.Customer!.CustomerName.ToLower().Contains(searchValue) ||
                            sv.Customer.CustomerTerms.ToLower().Contains(searchValue) ||
                            sv.Service!.Name.ToLower().Contains(searchValue) ||
                            sv.Service.ServiceNo.ToString().Contains(searchValue) ||
                            sv.Period.ToString("MMM yyyy").ToLower().Contains(searchValue) ||
                            sv.Amount.ToString(CultureInfo.InvariantCulture).Contains(searchValue) ||
                            sv.Instructions?.ToLower().Contains(searchValue) == true ||
                            sv.CreatedBy!.ToLower().Contains(searchValue)
                            )
                        .ToList();
                }
                // Sorting
                if (parameters.Order != null && parameters.Order.Count > 0)
                {
                    var orderColumn = parameters.Order[0];
                    var columnName = parameters.Columns[orderColumn.Column].Data;
                    var sortDirection = orderColumn.Dir.ToLower() == "asc" ? "ascending" : "descending";
                    serviceInvoices = serviceInvoices
                        .AsQueryable()
                        .OrderBy($"{columnName} {sortDirection}")
                        .ToList();
                }
                var totalRecords = serviceInvoices.Count();
                var pagedData = serviceInvoices
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
        public async Task<IActionResult> GetAllServiceInvoiceIds(CancellationToken  cancellationToken)
        {
            var invoiceIds = await _dbContext.ServiceInvoices
                                     .Select(sv => sv.ServiceInvoiceId) // Assuming Id is the primary key
                                     .ToListAsync(cancellationToken);
            return Json(invoiceIds);
        }

        [HttpGet]
        public async Task<IActionResult> Create(CancellationToken cancellationToken)
        {
            var viewModel = new ServiceInvoice
            {
                Customers = await _dbContext.Customers
                    .OrderBy(c => c.CustomerId)
                    .Select(c => new SelectListItem
                    {
                        Value = c.CustomerId.ToString(),
                        Text = c.CustomerName
                    })
                    .ToListAsync(cancellationToken),
                Services = await _dbContext.Services
                    .OrderBy(s => s.ServiceId)
                    .Select(s => new SelectListItem
                    {
                        Value = s.ServiceId.ToString(),
                        Text = s.Name
                    })
                    .ToListAsync(cancellationToken)
            };
            return View(viewModel);
        }

        [HttpPost]
        public async Task<IActionResult> Create(ServiceInvoice model, CancellationToken cancellationToken)
        {
            model.Customers = await _dbContext.Customers
                .OrderBy(c => c.CustomerId)
                .Select(c => new SelectListItem
                {
                    Value = c.CustomerId.ToString(),
                    Text = c.CustomerName
                })
                .ToListAsync(cancellationToken);
            model.Services = await _dbContext.Services
                .OrderBy(s => s.ServiceId)
                .Select(s => new SelectListItem
                {
                    Value = s.ServiceId.ToString(),
                    Text = s.Name
                })
                .ToListAsync(cancellationToken);
            if (ModelState.IsValid)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
                try
                {
                    #region --Validating the series

                    var generateSvNo = await _serviceInvoiceRepo.GenerateSvNo(cancellationToken);
                    var getLastNumber = long.Parse(generateSvNo.Substring(2));

                    if (getLastNumber > 9999999999)
                    {
                        TempData["error"] = "You reach the maximum Series Number";
                        return View(model);
                    }
                    var totalRemainingSeries = 9999999999 - getLastNumber;
                    if (getLastNumber >= 9999999899)
                    {
                        TempData["warning"] = $"Service invoice created successfully, Warning {totalRemainingSeries} series number remaining";
                    }
                    else
                    {
                        TempData["success"] = "Service invoice created successfully";
                    }

                    #endregion --Validating the series

                    #region --Retrieval of Services

                    var services = await _serviceInvoiceRepo.GetServicesAsync(model.ServicesId, cancellationToken);

                    #endregion --Retrieval of Services

                    #region --Retrieval of Customer

                    var customer = await _serviceInvoiceRepo.FindCustomerAsync(model.CustomerId, cancellationToken);

                    #endregion --Retrieval of Customer

                    #region --Saving the default properties

                    model.ServiceInvoiceNo = generateSvNo;

                    model.CreatedBy = createdBy;

                    model.ServiceNo = services.ServiceNo;

                    model.Total = model.Amount;

                    if (DateOnly.FromDateTime(model.CreatedDate) < model.Period)
                    {
                        model.UnearnedAmount += model.Amount;
                    }
                    else
                    {
                        model.CurrentAndPreviousAmount += model.Amount;
                    }

                    if (customer.CustomerType == "Vatable")
                    {
                        model.CurrentAndPreviousAmount = Math.Round(model.CurrentAndPreviousAmount / 1.12m, 2);
                        model.UnearnedAmount = Math.Round(model.UnearnedAmount / 1.12m, 2);

                        var total = model.CurrentAndPreviousAmount + model.UnearnedAmount;

                        var netOfVatAmount = _generalRepo.ComputeNetOfVat(model.Amount);
                        var roundedNetAmount = Math.Round(netOfVatAmount, 2);

                        if (roundedNetAmount > total)
                        {
                            var shortAmount = netOfVatAmount - total;

                            model.CurrentAndPreviousAmount += shortAmount;
                        }
                    }

                    await _dbContext.AddAsync(model, cancellationToken);

                    #endregion --Saving the default properties

                    #region --Audit Trail Recording

                    if (model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId == 0)
                    {
                        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                        AuditTrail auditTrailBook = new(createdBy, $"Create new service invoice# {model.ServiceInvoiceNo}", "Service Invoice", ipAddress!);
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

            return View(model);
        }

        public async Task<IActionResult> PrintInvoice(int id, CancellationToken cancellationToken)
        {
            var soa = await _serviceInvoiceRepo
                .FindSv(id, cancellationToken);

            return View(soa);
        }

        public async Task<IActionResult> PrintedInvoice(int id, CancellationToken cancellationToken)
        {
            var findIdOfSoa = await _serviceInvoiceRepo.FindSv(id, cancellationToken);
            var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
            if (!findIdOfSoa.IsPrinted)
            {

                #region --Audit Trail Recording

                if (findIdOfSoa.OriginalSeriesNumber.IsNullOrEmpty() && findIdOfSoa.OriginalDocumentId == 0)
                {
                    var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                    AuditTrail auditTrailBook = new(createdBy, $"Printed original copy of sv# {findIdOfSoa.ServiceInvoiceNo}", "Service Invoice", ipAddress!);
                    await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                }

                #endregion --Audit Trail Recording

                findIdOfSoa.IsPrinted = true;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            return RedirectToAction(nameof(PrintInvoice), new { id });
        }

        public async Task<IActionResult> Post(int id, CancellationToken cancellationToken)
        {
            var model = await _serviceInvoiceRepo.FindSv(id, cancellationToken);
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var createdBy = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.PostedBy : await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
            var date = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.PostedDate : DateTime.Now;
            try
            {
                if (!model.IsPosted)
                {
                    model.IsPosted = true;
                    model.PostedBy = createdBy;
                    model.PostedDate = date;

                    #region --SV Date Computation--

                    var postedDate = DateOnly.FromDateTime(model.CreatedDate) >= model.Period ? DateOnly.FromDateTime(model.CreatedDate) : model.Period.AddMonths(1).AddDays(-1);

                    #endregion --SV Date Computation--

                    #region --Sales Book Recording

                    decimal withHoldingTaxAmount = 0;
                    decimal withHoldingVatAmount = 0;
                    decimal netOfVatAmount;
                    decimal vatAmount = 0;

                    if (model.Customer!.CustomerType == CS.VatType_Vatable)
                    {
                        netOfVatAmount = _generalRepo.ComputeNetOfVat(model.Total);
                        vatAmount = _generalRepo.ComputeVatAmount(netOfVatAmount);
                    }
                    else
                    {
                        netOfVatAmount = model.Total;
                    }

                    if (model.Customer.WithHoldingTax)
                    {
                        withHoldingTaxAmount = _generalRepo.ComputeEwtAmount(netOfVatAmount, 0.01m);
                    }

                    if (model.Customer.WithHoldingVat)
                    {
                        withHoldingVatAmount = _generalRepo.ComputeEwtAmount(netOfVatAmount, 0.05m);
                    }

                    var sales = new SalesBook();

                    if (model.Customer.CustomerType == "Vatable")
                    {
                        sales.TransactionDate = postedDate;
                        sales.SerialNo = model.ServiceInvoiceNo!;
                        sales.SoldTo = model.Customer.CustomerName;
                        sales.TinNo = model.Customer.CustomerTin;
                        sales.Address = model.Customer.CustomerAddress;
                        sales.Description = model.Service!.Name;
                        sales.Amount = model.Total;
                        sales.VatAmount = vatAmount;
                        sales.VatableSales = netOfVatAmount;
                        sales.Discount = model.Discount;
                        sales.NetSales = netOfVatAmount;
                        sales.CreatedBy = model.CreatedBy;
                        sales.CreatedDate = model.CreatedDate;
                        sales.DueDate = model.DueDate;
                        sales.DocumentId = model.ServiceInvoiceId;
                    }
                    else if (model.Customer.CustomerType == "Exempt")
                    {
                        sales.TransactionDate = postedDate;
                        sales.SerialNo = model.ServiceInvoiceNo!;
                        sales.SoldTo = model.Customer.CustomerName;
                        sales.TinNo = model.Customer.CustomerTin;
                        sales.Address = model.Customer.CustomerAddress;
                        sales.Description = model.Service!.Name;
                        sales.Amount = model.Total;
                        sales.VatExemptSales = model.Total;
                        sales.Discount = model.Discount;
                        sales.NetSales = netOfVatAmount;
                        sales.CreatedBy = model.CreatedBy;
                        sales.CreatedDate = model.CreatedDate;
                        sales.DueDate = model.DueDate;
                        sales.DocumentId = model.ServiceInvoiceId;
                    }
                    else
                    {
                        sales.TransactionDate = postedDate;
                        sales.SerialNo = model.ServiceInvoiceNo!;
                        sales.SoldTo = model.Customer.CustomerName;
                        sales.TinNo = model.Customer.CustomerTin;
                        sales.Address = model.Customer.CustomerAddress;
                        sales.Description = model.Service!.Name;
                        sales.Amount = model.Total;
                        sales.ZeroRated = model.Total;
                        sales.Discount = model.Discount;
                        sales.NetSales = netOfVatAmount;
                        sales.CreatedBy = model.CreatedBy;
                        sales.CreatedDate = model.CreatedDate;
                        sales.DueDate = model.DueDate;
                        sales.DocumentId = model.ServiceInvoiceId;
                    }

                    await _dbContext.AddAsync(sales, cancellationToken);

                    #endregion --Sales Book Recording

                    #region --General Ledger Book Recording

                    var ledgers = new List<GeneralLedgerBook>();
                    var accountTitlesDto = await _generalRepo.GetListOfAccountTitleDto(cancellationToken);
                    var arNonTradeTitle = accountTitlesDto.Find(c => c.AccountNumber == "101020500") ?? throw new ArgumentException("Account title '101020500' not found.");
                    var arTradeCwt = accountTitlesDto.Find(c => c.AccountNumber == "101020200") ?? throw new ArgumentException("Account title '101020200' not found.");
                    var arTradeCwv = accountTitlesDto.Find(c => c.AccountNumber == "101020300") ?? throw new ArgumentException("Account title '101020300' not found.");
                    var vatOutputTitle = accountTitlesDto.Find(c => c.AccountNumber == "201030100") ?? throw new ArgumentException("Account title '201030100' not found.");

                    ledgers.Add(
                            new GeneralLedgerBook
                            {
                                Date = postedDate,
                                Reference = model.ServiceInvoiceNo!,
                                Description = model.Service.Name,
                                AccountNo = arNonTradeTitle.AccountNumber,
                                AccountTitle = arNonTradeTitle.AccountName,
                                Debit = Math.Round(model.Total - (withHoldingTaxAmount + withHoldingVatAmount), 2),
                                Credit = 0,
                                CreatedBy = model.CreatedBy,
                                CreatedDate = model.CreatedDate
                            }
                        );
                    if (withHoldingTaxAmount > 0)
                    {
                        ledgers.Add(
                            new GeneralLedgerBook
                            {
                                Date = postedDate,
                                Reference = model.ServiceInvoiceNo!,
                                Description = model.Service.Name,
                                AccountNo = arTradeCwt.AccountNumber,
                                AccountTitle = arTradeCwt.AccountName,
                                Debit = withHoldingTaxAmount,
                                Credit = 0,
                                CreatedBy = model.CreatedBy,
                                CreatedDate = model.CreatedDate
                            }
                        );
                    }
                    if (withHoldingVatAmount > 0)
                    {
                        ledgers.Add(
                            new GeneralLedgerBook
                            {
                                Date = postedDate,
                                Reference = model.ServiceInvoiceNo!,
                                Description = model.Service.Name,
                                AccountNo = arTradeCwv.AccountNumber,
                                AccountTitle = arTradeCwv.AccountName,
                                Debit = withHoldingVatAmount,
                                Credit = 0,
                                CreatedBy = model.CreatedBy,
                                CreatedDate = model.CreatedDate
                            }
                        );
                    }

                    ledgers.Add(
                           new GeneralLedgerBook
                           {
                               Date = postedDate,
                               Reference = model.ServiceInvoiceNo!,
                               Description = model.Service.Name,
                               AccountNo = model.Service.CurrentAndPreviousNo!,
                               AccountTitle = model.Service.CurrentAndPreviousTitle!,
                               Debit = 0,
                               Credit = Math.Round((netOfVatAmount), 2),
                               CreatedBy = model.CreatedBy,
                               CreatedDate = model.CreatedDate
                           }
                       );

                    if (vatAmount > 0)
                    {
                        ledgers.Add(
                            new GeneralLedgerBook
                            {
                                Date = postedDate,
                                Reference = model.ServiceInvoiceNo!,
                                Description = model.Service.Name,
                                AccountNo = vatOutputTitle.AccountNumber,
                                AccountTitle = vatOutputTitle.AccountName,
                                Debit = 0,
                                Credit = Math.Round((vatAmount), 2),
                                CreatedBy = model.CreatedBy,
                                CreatedDate = model.CreatedDate
                            }
                        );
                    }

                    if (!_generalRepo.IsJournalEntriesBalanced(ledgers))
                    {
                        throw new ArgumentException("Debit and Credit is not equal, check your entries.");
                    }

                    await _dbContext.GeneralLedgerBooks.AddRangeAsync(ledgers, cancellationToken);

                    #endregion --General Ledger Book Recording

                    #region --Audit Trail Recording

                    if (model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId == 0)
                    {
                        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                        AuditTrail auditTrailBook = new(createdBy, $"Posted service invoice# {model.ServiceInvoiceNo}", "Service Invoice", ipAddress!);
                        await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                    }

                    #endregion --Audit Trail Recording

                    await _dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    TempData["success"] = "Service invoice has been posted.";
                    return RedirectToAction(nameof(PrintInvoice), new { id });
                }

                return RedirectToAction(nameof(PrintInvoice), new { id });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                TempData["error"] = ex.Message;
                return RedirectToAction(nameof(PrintInvoice), new { id });
            }
        }

        public async Task<IActionResult> Cancel(int id, string cancellationRemarks, CancellationToken cancellationToken)
        {
            var model = await _dbContext.ServiceInvoices.FirstOrDefaultAsync(x => x.ServiceInvoiceId == id, cancellationToken);
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
                            AuditTrail auditTrailBook = new(createdBy, $"Cancelled service invoice# {model.ServiceInvoiceNo}", "Service Invoice", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        TempData["success"] = "Service invoice has been Cancelled.";
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

        public async Task<IActionResult> Void(int id, CancellationToken cancellationToken)
        {
            var model = await _dbContext.ServiceInvoices.FirstOrDefaultAsync(x => x.ServiceInvoiceId == id, cancellationToken);
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
                            AuditTrail auditTrailBook = new(createdBy, $"Voided service invoice# {model.ServiceInvoiceNo}", "Service Invoice", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        await _generalRepo.RemoveRecords<SalesBook>(gl => gl.SerialNo == model.ServiceInvoiceNo, cancellationToken);
                        await _generalRepo.RemoveRecords<GeneralLedgerBook>(gl => gl.Reference == model.ServiceInvoiceNo, cancellationToken);

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        TempData["success"] = "Service invoice has been voided.";
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
        public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken)
        {
            if (id == 0)
            {
                return NotFound();
            }
            var existingModel = await _serviceInvoiceRepo.FindSv(id, cancellationToken);

            existingModel.Customers = await _dbContext.Customers
                .OrderBy(c => c.CustomerId)
                .Select(c => new SelectListItem
                {
                    Value = c.CustomerId.ToString(),
                    Text = c.CustomerName
                })
                .ToListAsync(cancellationToken);
            existingModel.Services = await _dbContext.Services
                .OrderBy(s => s.ServiceId)
                .Select(s => new SelectListItem
                {
                    Value = s.ServiceId.ToString(),
                    Text = s.Name
                })
                .ToListAsync(cancellationToken);

            return View(existingModel);
        }

        [HttpPost]
        public async Task<IActionResult> Edit(ServiceInvoice model, CancellationToken cancellationToken)
        {
            var existingModel = await _serviceInvoiceRepo.FindSv(model.ServiceInvoiceId, cancellationToken);

            if (ModelState.IsValid)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
                try
                {
                    #region --Saving the default properties

                    existingModel.Discount = model.Discount;
                    existingModel.Amount = model.Amount;
                    existingModel.Period = model.Period;
                    existingModel.DueDate = model.DueDate;
                    existingModel.Instructions = model.Instructions;

                    decimal total = 0;
                    total += model.Amount;
                    existingModel.Total = total;

                    #endregion --Saving the default properties

                    if (_dbContext.ChangeTracker.HasChanges())
                    {
                        #region --Audit Trail Recording

                        if (model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId == 0)
                        {
                            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                            AuditTrail auditTrailBook = new(createdBy, $"Edited service invoice# {existingModel.ServiceInvoiceNo}", "Service Invoice", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        TempData["success"] = "Service Invoice updated successfully";
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
                    existingModel.Customers = await _dbContext.Customers
                        .OrderBy(c => c.CustomerId)
                        .Select(c => new SelectListItem
                        {
                            Value = c.CustomerId.ToString(),
                            Text = c.CustomerName
                        })
                        .ToListAsync(cancellationToken);
                    existingModel.Services = await _dbContext.Services
                        .OrderBy(s => s.ServiceId)
                        .Select(s => new SelectListItem
                        {
                            Value = s.ServiceId.ToString(),
                            Text = s.Name
                        })
                        .ToListAsync(cancellationToken);
                    TempData["error"] = ex.Message;
                    return View(existingModel);
                }
            }

            existingModel.Customers = await _dbContext.Customers
                .OrderBy(c => c.CustomerId)
                .Select(c => new SelectListItem
                {
                    Value = c.CustomerId.ToString(),
                    Text = c.CustomerName
                })
                .ToListAsync(cancellationToken);
            existingModel.Services = await _dbContext.Services
                .OrderBy(s => s.ServiceId)
                .Select(s => new SelectListItem
                {
                    Value = s.ServiceId.ToString(),
                    Text = s.Name
                })
                .ToListAsync(cancellationToken);
            return View(existingModel);
        }
    }
}
