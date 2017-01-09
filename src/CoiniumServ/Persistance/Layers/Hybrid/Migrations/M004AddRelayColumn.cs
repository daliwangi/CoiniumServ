using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentMigrator;

namespace CoiniumServ.Persistance.Layers.Hybrid.Migrations
{
    /// <summary>
    /// add a column named IsRelayBlock
    /// </summary>
    [Migration(20150425)]
    public class M004AddRelayColumn:Migration
    {
        public override void Up()
        {
            Alter.Table("Block").AddColumn("IsRelayBlock").AsBoolean().NotNullable().WithDefaultValue("0");
        }

        public override void Down()
        {
            Delete.Column("IsRelayBlock").FromTable("Block");
        }
    }
}
