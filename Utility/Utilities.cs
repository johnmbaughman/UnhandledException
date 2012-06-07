#region

// -----------------------------------------------------
// MIT License
// Copyright (C) 2012 John M. Baughman (jbaughmanphoto.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
// associated documentation files (the "Software"), to deal in the Software without restriction,
// including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
// subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or substantial
// portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
// LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
// IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
// SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// -----------------------------------------------------

#endregion

using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Service.Core.ExceptionHandler.Utility {
	public class Utilities {
		#region Collection IsNullOrEmpty method.

		[MethodImpl(MethodImplOptions.NoInlining)]
		public static bool IsNullOrEmpty(ICollection obj) {
			return (obj == null || obj.Count == 0);
		}

		#endregion Collection IsNullOrEmpty method.

		public static IPAddress GetIPv4Address() {
			IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());

			foreach (IPAddress address in host.AddressList) {
				if (address.AddressFamily.ToString() == "InterNetwork")
					return address;
			}

			return null;
		}

		public static DateTime AssemblyBuildDate(Assembly assembly, bool forceFileDate = false) {
			Version version = assembly.GetName().Version;
			DateTime build;

			if (forceFileDate) {
				return AssemblyFileTime(assembly);
			}
			else {
				build = DateTime.Parse("01/01/2000").AddDays(version.Build).AddSeconds(version.Revision * 2);
				if (TimeZone.IsDaylightSavingTime(DateTime.Now, TimeZone.CurrentTimeZone.GetDaylightChanges(DateTime.Now.Year))) {
					build = build.AddHours(1);
				}

				if (build > DateTime.Now || version.Build < 730 || version.Revision == 0) {
					build = AssemblyFileTime(assembly);
				}
			}

			return build;
		}

		private static DateTime AssemblyFileTime(Assembly assembly) {
			try {
				return File.GetLastWriteTime(assembly.Location);
			}
			catch {
				return DateTime.MaxValue;
			}
		}
	}
}