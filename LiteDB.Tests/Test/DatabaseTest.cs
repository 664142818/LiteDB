using FluentAssertions;
using FluentAssertions.Equivalency;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Xunit;
using LiteDB;

namespace LiteDB.Tests.Test
{
    public class DatabaseTest
    {
        [Fact]
        public void Test1()
        {
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