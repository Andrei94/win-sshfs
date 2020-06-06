#region

using System;
using System.Diagnostics;
using System.Threading;

#endregion

namespace Sshfs
{
	internal static class Program
	{
		/// <summary>
		///   The main entry point for the application.
		/// </summary>
		[STAThread]
		private static void Main(params string[] args)
		{
#if DEBUG
			Debug.AutoFlush = true;
			//Debug.Listeners.Clear();
			//Debug.Listeners.Add(new DelimitedListTraceListener(String.Format("{0}\\log{1:yyyy-MM-dd-HH-mm-ss}.txt",Environment.CurrentDirectory,DateTime.Now), "debug"));
			Debug.Listeners.Add(new DelimitedListTraceListener(Environment.CurrentDirectory + "\\last.log", "debug"));
			//Debug.Listeners.Add(Console.Out);
#endif
#if DEBUG && DEBUGSHADOWCOPY
            string shadowCopyDir = Environment.CurrentDirectory + "\\debug-shadow";
            if (Directory.Exists(shadowCopyDir))
            {
                Directory.Delete(shadowCopyDir, true);
            }
#endif
#if DEBUG
			args = new[]
			{
				"files", "22", "username", "3.122.249.25", "username", "nlcTjt6XnZe7bfKfHUnAwvH/4xyt2o1Y"
			};
#endif
			while(true)
			{
				try
				{
					SftpApplication.Run(args);
				}
				catch(Exception)
				{
				}
			}
		}
	}
}