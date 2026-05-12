using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Test
{
    public class DatabaseTest
    {
        [Fact]
        public void Test1()
        {
            // 替换原来的内存数据库或临时文件代码
            using (var db = new LiteDatabase(@"D:\Net\mytest.db"))
            {
                var col = db.GetCollection<Customer>("customers");
                var customer = new Customer { Name = "John Doe", Phones = new[] { "8000-0000" }, IsActive = true };
                col.Insert(customer);
            }
        }

        [Fact]
        public void Test2()
        {


        }

        [Fact]
        public void Test3()
        {


        }


    }
}