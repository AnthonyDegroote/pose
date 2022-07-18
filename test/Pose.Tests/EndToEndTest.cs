using System;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Pose.Tests
{
    [TestClass]
    public class EndToEndTest
    {
        [TestMethod]
        public void TestConsoleStaticMethod()
        {
            TextWriter writer = Console.Out;
            // Arrange
            Shim consoleShim = Shim
                .Replace(() => Console.WriteLine(Is.A<string>()))
                .With(delegate (string s) { Console.WriteLine("Hijacked: {0}", s); });
            
            // Act
            PoseContext.Isolate(() =>
            {
                Console.WriteLine("Hello world");
            }, consoleShim);

            // Assert
            Assert.AreEqual("Hijacked: Hello World", writer.ToString());
        }

        [TestMethod]
        public void TestDateTimeNowStaticMethod()
        {
            TextWriter writer = Console.Out;
            // Arrange
            Shim dateTimeShim = Shim
                .Replace(() => DateTime.Now)
                .With(() => new DateTime(2004, 4, 4));

            // Act
            PoseContext.Isolate(() =>
            {
                Console.WriteLine(DateTime.Now);
            }, dateTimeShim);

            // Assert
            Assert.AreEqual("04/04/2004 00:00:00", writer.ToString());
        }

        [TestMethod]
        public void TestStaticMethod()
        {
            // Arrange
            Shim shim = Shim
                .Replace(() => ClassWithStaticMethod.StaticMethod(Is.A<string>()))
                .With(delegate (string parameter) { return true; });

            bool result = false;

            ClassWithStaticMethod classTested = new ClassWithStaticMethod();

            // Act
            PoseContext.Isolate(() =>
            {
                result = classTested.ExposedMethod("");
            }, shim);

            // Assert
            Assert.IsTrue(result);
        }
    }
}
