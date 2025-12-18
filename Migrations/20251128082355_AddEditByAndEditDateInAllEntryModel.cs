using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Accounting_System.Migrations
{
    /// <inheritdoc />
    public partial class AddEditByAndEditDateInAllEntryModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "edited_by",
                table: "service_invoices",
                type: "varchar(50)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "edited_date",
                table: "service_invoices",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "edited_by",
                table: "sales_invoices",
                type: "varchar(50)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "edited_date",
                table: "sales_invoices",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "edited_by",
                table: "receiving_reports",
                type: "varchar(50)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "edited_date",
                table: "receiving_reports",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "edited_by",
                table: "purchase_orders",
                type: "varchar(50)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "edited_date",
                table: "purchase_orders",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "edited_by",
                table: "journal_voucher_headers",
                type: "varchar(50)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "edited_date",
                table: "journal_voucher_headers",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "edited_by",
                table: "debit_memos",
                type: "varchar(50)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "edited_date",
                table: "debit_memos",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "edited_by",
                table: "credit_memos",
                type: "varchar(50)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "edited_date",
                table: "credit_memos",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "edited_by",
                table: "collection_receipts",
                type: "varchar(50)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "edited_date",
                table: "collection_receipts",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "edited_by",
                table: "check_voucher_headers",
                type: "varchar(50)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "edited_date",
                table: "check_voucher_headers",
                type: "timestamp without time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "edited_by",
                table: "service_invoices");

            migrationBuilder.DropColumn(
                name: "edited_date",
                table: "service_invoices");

            migrationBuilder.DropColumn(
                name: "edited_by",
                table: "sales_invoices");

            migrationBuilder.DropColumn(
                name: "edited_date",
                table: "sales_invoices");

            migrationBuilder.DropColumn(
                name: "edited_by",
                table: "receiving_reports");

            migrationBuilder.DropColumn(
                name: "edited_date",
                table: "receiving_reports");

            migrationBuilder.DropColumn(
                name: "edited_by",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "edited_date",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "edited_by",
                table: "journal_voucher_headers");

            migrationBuilder.DropColumn(
                name: "edited_date",
                table: "journal_voucher_headers");

            migrationBuilder.DropColumn(
                name: "edited_by",
                table: "debit_memos");

            migrationBuilder.DropColumn(
                name: "edited_date",
                table: "debit_memos");

            migrationBuilder.DropColumn(
                name: "edited_by",
                table: "credit_memos");

            migrationBuilder.DropColumn(
                name: "edited_date",
                table: "credit_memos");

            migrationBuilder.DropColumn(
                name: "edited_by",
                table: "collection_receipts");

            migrationBuilder.DropColumn(
                name: "edited_date",
                table: "collection_receipts");

            migrationBuilder.DropColumn(
                name: "edited_by",
                table: "check_voucher_headers");

            migrationBuilder.DropColumn(
                name: "edited_date",
                table: "check_voucher_headers");
        }
    }
}
