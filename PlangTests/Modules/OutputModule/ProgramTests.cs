﻿using Microsoft.VisualStudio.TestTools.UnitTesting;
using PLang.Modules.OutputModule;
using NSubstitute;
using PLang.Services.OutputStream;

namespace PLangTests.Modules.OutputModule
{
    [TestClass]
	public class ProgramTests : BasePLangTest
	{
		[TestInitialize]
		public void Init() {
			base.Initialize();

		}

		[TestMethod]
		public async Task Ask_Test()
		{
			throw new Exception("Needs fixing");
		//	outputStream.Ask(Arg.Any<string>()).Returns(new Task<(string, PLang.Errors.IError)>("good", null));
			var p = new Program(outputStreamFactory, outputSystemStreamFactory, variableHelper, programFactory);
			var result = await p.Ask("Hello, how are your?");

			Assert.AreEqual("good", result.Item1);
		}

		[TestMethod]
		public async Task Write_Test()
		{			
			var p = new Program(outputStreamFactory, outputSystemStreamFactory, variableHelper, programFactory);
			await p.Write("Hello, how are your?");

			await outputStream.Received(1).Write(Arg.Any<object>());
		}

	}
}
