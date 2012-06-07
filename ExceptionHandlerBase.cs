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
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Net.Mail;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Caching;
using System.Web.SessionState;
using Service.Core.ExceptionHandler.Utility;
using Service.Core.Log;
using Service.Core.Log.Configuration;

namespace Service.Core.ExceptionHandler {
	public abstract class ExceptionHandlerBase<THandler> {
		protected readonly NameValueCollection ResultCollection = new NameValueCollection();
		protected string Exception = string.Empty;
		protected string ExceptionType = string.Empty;
		protected string ViewState = string.Empty;
		internal Settings Settings = new Settings();

		#region Static Utility methods

		public static Exception GetLowestException(Exception ex) {
			return null == ex.InnerException ? ex : GetLowestException(ex.InnerException);
		}

		#endregion Static Utility methods

		#region StackFrame & StackTrace

		// Turns a single stack frame object in an informative string
		private static string StackFrameToString(StackFrame sf) {
			StringBuilder sb = new StringBuilder();
			int intParam = 0;
			MemberInfo mi = sf.GetMethod();

			// Build method name
			sb.AppendFormat("    {0}.{1}.{2}", mi.DeclaringType.Namespace, mi.DeclaringType.Name, mi.Name);

			// Build method params
			sb.Append("(");
			foreach (ParameterInfo param in sf.GetMethod().GetParameters()) {
				intParam++;
				if (intParam > 1) {
					sb.Append(",");
				}

				sb.AppendFormat("{0} {1}", param.ParameterType.Name, param.Name);
			}
			sb.Append("){0}\n");

			// If source code is available, append location info
			sb.Append("       ");
			if (string.IsNullOrEmpty(sf.GetFileName())) {
				sb.Append("(unknown file)");
				// Native code offset is always available.
				sb.AppendFormat(": N {0:#00000}", sf.GetNativeOffset());
			}
			else {
				sb.AppendFormat("{0}: line {1:#00000}, col {2:#00}", Path.GetFileName(sf.GetFileName()), sf.GetFileLineNumber(), sf.GetFileColumnNumber());
				// If IL is available, append IL location info
				if (sf.GetILOffset() != StackFrame.OFFSET_UNKNOWN) {
					sb.AppendFormat(", IL {0:#00000}", sf.GetILOffset());
				}
			}

			sb.Append("\n");

			return sb.ToString();
		}

		// Enhanced stack trace generator
		private static string EnhancedStackTrace(StackTrace st, string skipClassNames) {
			StringBuilder sb = new StringBuilder();

			sb.Append("\n---- Stack Trace ----\n");

			for (int frame = 0; frame < st.FrameCount; frame++) {
				StackFrame sf = st.GetFrame(frame);
				MemberInfo mi = sf.GetMethod();

				if (!string.IsNullOrEmpty(skipClassNames) && mi.DeclaringType.Name.IndexOf(skipClassNames) > -1) {
					// Ignore these frames
				}
				else {
					sb.Append(StackFrameToString(sf));
				}
			}

			sb.Append("\n");

			return sb.ToString();
		}

		// Enhanced stack trace generator with default skipClassNames parameter.
		// Original VB.NET code used the Optional and default value settings, things C# doesn't provide (thank god).
		private static string EnhancedStackTrace(StackTrace st) {
			return EnhancedStackTrace(st, string.Empty);
		}

		// Enhanced stack trace generator using existing exception as start point.
		private static string EnhancedStackTrace(Exception ex) {
			return EnhancedStackTrace(new StackTrace(ex, true));
		}

		// Enhanced stack trace generator using current execution as start point.
		private static string EnhancedStackTrace() {
			return EnhancedStackTrace(new StackTrace(true), "UeUnhandledException");
		}

		#endregion StackFrame & StackTrace

		public void HandleException(Exception exception) {
			// Don't bother us wth debug exceptions (eg. those running on localhost)
			if (Settings.IgnoreDebugErrors) {
				if (Debugger.IsAttached) {
					return;
				}

				if (Settings.IsWebApp) {
					string host = HttpContext.Current.Request.Url.Host.ToLower();
					if (host == "localhost" || host == "127.0.0.1") {
						return;
					}
				}
			}

			// Turn the exception into an informative string
			try {
				Exception = ExceptionToString(exception);
				ExceptionType = exception.GetType().FullName;

				// Ignore root exceptions
				if (ExceptionType == Settings.RootException || ExceptionType == Settings.RootWsException) {
					if (null != exception.InnerException) {
						ExceptionType = exception.InnerException.GetType().FullName;
					}
				}
			}
			catch (Exception ex) {
				Exception = String.Format("Error '{0}' while generating exception string", ex.Message);
			}

			// We are going to ignore System.Web.HttpException errors. Not necessarily the best thing, but
			// for now it will work.
			if (Settings.IgnoreHttpErrors && ExceptionType == Settings.HttpException) {
				return;
			}

			// Some exceptions should be ignored: ones that match this regex
			// Note that we are using the entire full-text string of the exception to test regex against
			// so any part of the text can match.
			if (!string.IsNullOrEmpty(Settings.IgnoreRegExp)) {
				if (Regex.IsMatch(Exception, Settings.IgnoreRegExp, RegexOptions.IgnoreCase)) {
					return;
				}
			}

			// Log this error to various locations
			try {
				// Event logging takes < 100ms
				if (Settings.LogToEventLog) {
					ExceptionToEventLog();
				}

				// textfile logging takes < 50ms
				if (Settings.LogToFile) {
					ExceptionToFile();
				}

				// Email logging takes under 1 second
				if (Settings.LogToEmail) {
					ExceptionToEmail();
				}

				//Log exception to JBSAppMonitor DB
				if (Settings.LogToSQL) {
					SQLLogger.ExceptionToSQL(Exception, ExceptionType, new SQLLoggerConfiguration {
						ConnectionString = Settings.SQLConnectionString,
						ApplicationId = Settings.ApplicationID,
						LocationId = Settings.LocationID,
						ReportedBy = Settings.ReportedBy,
						SystemId = Settings.SystemID
					});
				}
			}
			catch {
				// Absorb the exception
				// Execution stops.
			}
		}

		#region Logging

		// Write exception to event log
		private bool ExceptionToEventLog() {
			try {
				EventLog.WriteEntry(WebCurrentUrl(), string.Format("\n{0}", Exception), EventLogEntryType.Error);

				return true;
			}
			catch (Exception ex) {
				ResultCollection.Add("LogToEventLog", ex.Message);
			}

			return false;
		}

		// Write exception to a text file
		private bool ExceptionToFile() {
			// TODO: Use LoggingEngine.FileLogger
			string logFileName = Settings.LogFileName;
			if (!string.IsNullOrEmpty(Path.GetFileName(logFileName))) {
				logFileName = Path.Combine(logFileName, Settings.DefaultLogName);
			}

			//StreamWriter sw = null;

			//try
			//{
			//    sw = new StreamWriter(LogFilePath, true);
			//    sw.Write(Exception);
			//    sw.WriteLine();
			//    sw.Close();

			//    return true;
			//}
			//catch (Exception ex)
			//{
			//    ResultCollection.Add("LogToFile", ex.Message);
			//}
			//finally
			//{
			//    if (null != sw)
			//    {
			//        sw.Close();
			//    }
			//}

			return false;
		}

		private bool ExceptionToEmail() {
			MailAddressCollection toList = Settings.EmailToAddressList;
			if (Utilities.IsNullOrEmpty(toList)) {
				// don't bother mailing if we don't have anyone to mail to..
				return true;
			}

			try {
				MailMessage message = new MailMessage() {
					From = new MailAddress(Settings.EmailFromAddress, Settings.EmailAddressFromName),
					Subject = string.Format("Security request system error - {0}", ExceptionType),
					Body = Exception
				};

				foreach (MailAddress address in Settings.EmailToAddressList) {
					message.To.Add(address);
				}

				SmtpClient client = new SmtpClient(Settings.EmailServer);
				client.UseDefaultCredentials = true;
				client.Send(message);

				return true;
			}
			catch (Exception ex) {
				ResultCollection.Add("LogToEmail", ex.Message);
				// we're in an unhandled exception handler
			}

			return false;
		}

		private string FormatDisplayString(string output) {
			string temp = string.IsNullOrEmpty(output) ? string.Empty : output;

			temp = temp.Replace("(app)", Settings.AppName);
			temp = temp.Replace("(contact)", Settings.ContactInfo);

			return temp;
		}

		// Writes text plus newline to http response stream.
		private void WriteLine(string line) {
			HttpContext.Current.Response.Write(string.Format("{0}\n", line));
		}

		private string FormatExceptionForUser() {
			StringBuilder sb = new StringBuilder();
			const string bullet = "·";

			sb.Append("\nThe following information about the error was automatically captured: \n\n");
			if (Settings.LogToEventLog) {
				sb.AppendFormat(" {0} ", bullet);
				if (string.IsNullOrEmpty(ResultCollection["LogToEventLog"])) {
					sb.Append("an event was written to the application log");
				}
				else {
					sb.Append("an event could NOT be written to the application log due to an error:");
					sb.AppendFormat("\n   '{0}'\n", ResultCollection["LogToEventLog"]);
				}
				sb.Append("\n");
			}

			if (Settings.LogToFile) {
				sb.AppendFormat(" {0} ", bullet);
				if (string.IsNullOrEmpty(ResultCollection["LogToFile"])) {
					sb.AppendFormat("details were written to a text log at:\n   {0}\n", Settings.LogFileName);
				}
				else {
					sb.AppendFormat("details could NOT be written to the text log due an error:\n   '{0}'\n", ResultCollection["LogToFile"]);
				}
			}

			if (Settings.LogToEmail) {
				sb.AppendFormat(" {0} ", bullet);
				if (string.IsNullOrEmpty(ResultCollection["LogToEmail"])) {
					sb.AppendFormat("an email was sent to: {0}\n", Settings.EmailToList);
				}
				else {
					sb.AppendFormat("email could NOT be sent due to an error:\n  '{0}'\n", ResultCollection["LogToEmail"]);
				}
			}

			sb.AppendFormat("\n\nDetailed error information follows:\n\n{0}", Exception);

			return sb.ToString();
		}

		#endregion Logging

		#region Identities

		// Exception safe WindowsIdentity.GetCurrent retrieval; returns "domain/username"
		// per MS, this sometimes randomly fails with "Access Denied" on NT4: exception handling included for legacy sake, may still throw anyway.
		private static string CurrentWindowsIdentity() {
			try {
				return System.Security.Principal.WindowsIdentity.GetCurrent().Name;
			}
			catch {
				return string.Empty;
			}
		}

		// Exception safe System.Environment retrieval; returns "domain/username"
		private static string CurrentEnvironmentIdentity() {
			try {
				return string.Format("{0}\\{1}", Environment.UserDomainName, Environment.UserName);
			}
			catch {
				return string.Empty;
			}
		}

		// Retrieve Process identity with fallback on error to safer method
		private static string ProcessIdentity() {
			string strTemp = CurrentWindowsIdentity();
			return string.IsNullOrEmpty(strTemp) ? CurrentEnvironmentIdentity() : strTemp;
		}

		#endregion Identities

		#region Web URL

		public static string RedirectUrl(string redirectPage) {
			string currentPage = WebCurrentUrl();
			string currentSite = WebCurrentUrlNoPage();
			return string.Format("{0}{1}?p={2}", currentSite, redirectPage, currentSite);
		}

		public static string EncodedRedirectUrl(string redirectPage) {
			string currentPage = HttpUtility.UrlEncode(WebCurrentUrl());
			string currentSite = WebCurrentUrlNoPage();
			return string.Format("{0}{1}?p={2}", currentSite, redirectPage, currentPage);
		}

		// returns the current URL; "http://localhost:85/mypath/mypage.aspx?test=1&apples=bear"
		private static string WebCurrentUrl() {
			NameValueCollection serverVariables = HttpContext.Current.Request.ServerVariables;

			// Need to add the appropriate protocol.
			string url = string.Format("{0}{1}", (serverVariables["HTTPS"] == "off") ? "http://" : "https://", serverVariables["SERVER_NAME"]);

			if (serverVariables["SERVER_PORT"] != "80") {
				url += string.Format(":{0}", serverVariables["SERVER_PORT"]);
			}

			url += serverVariables["URL"];

			if (serverVariables["QUERY_STRING"].Length > 0) {
				url += string.Format("?{0}", serverVariables["QUERY_STRING"]);
			}

			return url;
		}

		private static string WebCurrentUrlNoPage() {
			string currentUrl = WebCurrentUrl().Substring(0, WebCurrentUrl().IndexOf(".as"));
			return currentUrl.Substring(0, currentUrl.LastIndexOf("/") + 1);
		}

		#endregion Web URL

		#region Assembly info

		private static string AllAssemblyDetailsToString() {
			StringBuilder sb = new StringBuilder();

			const string lineFormat = "\n   {0, -30} {1, -15} {2}";

			sb.AppendFormat(lineFormat, "Assembly", "Version", "BuildDate");
			sb.AppendFormat(lineFormat, "--------", "-------", "---------");
			foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) {
				NameValueCollection nvc = AssemblyAttribs(a);
				if (nvc["Version"] != "0.0.0.0") {
					sb.AppendFormat(lineFormat, Path.GetFileName(nvc["CodeBase"]), nvc["Version"], nvc["BuildDate"]);
				}
			}

			sb.Append("\n");
			return sb.ToString();
		}

		private static string AssemblyDetailsToString(Assembly a) {
			StringBuilder sb = new StringBuilder();
			NameValueCollection nvc = AssemblyAttribs(a);

			sb.Append("Assembly Codebase:     ");
			try {
				sb.AppendFormat("{0}\n", nvc["CodeBase"]);
			}
			catch (Exception ex) {
				sb.AppendFormat("{0}\n", ex.Message);
			}

			sb.Append("Assembly Full Name:    ");
			try {
				sb.AppendFormat("{0}\n", nvc["FullName"]);
			}
			catch (Exception ex) {
				sb.AppendFormat("{0}\n", ex.Message);
			}

			sb.Append("Assembly Version:      ");
			try {
				sb.AppendFormat("{0}\n", nvc["Version"]);
			}
			catch (Exception ex) {
				sb.AppendFormat("{0}\n", ex.Message);
			}

			sb.Append("Assembly Build Date:   ");
			try {
				sb.AppendFormat("{0}\n", nvc["BuildDate"]);
			}
			catch (Exception ex) {
				sb.AppendFormat("{0}\n", ex.Message);
			}

			return sb.ToString();
		}

		private string AssemblyInfoToString(Exception ex) {
			// ex.source USUALLY contains the name of the assembly that generated the exception
			// at least, according to the MSDN documentation..
			Assembly a = GetAssemblyFromName(ex.Source);

			return null == a ? AllAssemblyDetailsToString() : AssemblyDetailsToString(a);
		}

		private static DateTime AssemblyLastWriteTime(Assembly a) {
			try {
				return File.GetLastWriteTime(a.Location);
			}
			catch {
				return DateTime.MaxValue;
			}
		}

		private static DateTime AssemblyBuildDate(Assembly a) {
			return AssemblyBuildDate(a, false);
		}

		private static DateTime AssemblyBuildDate(Assembly a, bool forceFileDate) {
			Version v = a.GetName().Version;
			DateTime dt;

			if (forceFileDate) {
				dt = AssemblyLastWriteTime(a);
			}
			else {
				DateTime.TryParse("01/01/2000", out dt);
				dt = dt.AddDays(v.Build).AddSeconds(v.Revision * 2);
				if (TimeZone.IsDaylightSavingTime(dt, TimeZone.CurrentTimeZone.GetDaylightChanges(dt.Year))) {
					dt = dt.AddHours(1);
				}

				if (dt > DateTime.Now || v.Build < 730 || v.Revision == 0) {
					dt = AssemblyLastWriteTime(a);
				}
			}

			return dt;
		}

		///<summary>
		///	returns string name / string value pair of all attribs for the specified assembly
		///</summary>
		///<remarks>
		///	note that Assembly* values are pulled from AssemblyInfo file in project folder
		///
		///	Trademark       = AssemblyTrademark string
		///	Debuggable      = True
		///	GUID            = 7FDF68D5-8C6F-44C9-B391-117B5AFB5467
		///	CLSCompliant    = True
		///	Product         = AssemblyProduct string
		///	Copyright       = AssemblyCopyright string
		///	Company         = AssemblyCompany string
		///	Description     = AssemblyDescription string
		///	Title           = AssemblyTitle string
		///</remarks>
		private static NameValueCollection AssemblyAttribs(Assembly a) {
			NameValueCollection nvc = new NameValueCollection();

			foreach (object attrib in a.GetCustomAttributes(false)) {
				string name = attrib.GetType().ToString();
				string value = string.Empty;

				switch (name) {
					case "System.Diagnostics.DebuggableAttribute":
						name = "Debuggable";
						value = ((DebuggableAttribute)attrib).IsJITTrackingEnabled.ToString();
						break;

					case "System.CLSCompliantAttribute":
						name = "CLSCompliant";
						value = ((CLSCompliantAttribute)attrib).IsCompliant.ToString();
						break;

					case "System.Runtime.InteropServices.GuidAttribute":
						name = "GUID";
						value = ((System.Runtime.InteropServices.GuidAttribute)attrib).Value;
						break;

					case "System.Reflection.AssemblyTrademarkAttribute":
						name = "Trademark";
						value = ((AssemblyTrademarkAttribute)attrib).Trademark;
						break;

					case "System.Reflection.AssemblyProductAttribute":
						name = "Product";
						value = ((AssemblyProductAttribute)attrib).Product;
						break;

					case "System.Reflection.AssemblyCopyrightAttribute":
						name = "Copyright";
						value = ((AssemblyCopyrightAttribute)attrib).Copyright;
						break;

					case "System.Reflection.AssemblyCompanyAttribute":
						name = "Company";
						value = ((AssemblyCompanyAttribute)attrib).Company;
						break;

					case "System.Reflection.AssemblyTitleAttribute":
						name = "Title";
						value = ((AssemblyTitleAttribute)attrib).Title;
						break;

					case "System.Reflection.AssemblyDescriptionAttribute":
						name = "Description";
						value = ((AssemblyDescriptionAttribute)attrib).Description;
						break;

					default:
						//            'Console.WriteLine(Name)
						break;
				}

				if (!string.IsNullOrEmpty(value)) {
					if (string.IsNullOrEmpty(nvc[name])) {
						nvc.Add(name, value);
					}
				}
			}

			// add some extra values that are not in the AssemblyInfo, but nice to have
			nvc.Add("CodeBase", a.CodeBase.Replace("file:///", string.Empty));
			nvc.Add("BuildDate", AssemblyBuildDate(a).ToString());
			nvc.Add("Version", a.GetName().Version.ToString());
			nvc.Add("FullName", a.FullName);

			return nvc;
		}

		///<summary>
		/// matches assembly by Assembly.GetName.Name; returns nothing if no match
		///</summary>
		private static Assembly GetAssemblyFromName(string assemblyName) {
			foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) {
				if (a.GetName().Name == assemblyName) {
					return a;
				}
			}

			return null;
		}

		#endregion Assembly info

		#region System info

		private string SysInfoToString(bool includeStackTrace = false) {
			StringBuilder sb = new StringBuilder();

			sb.AppendFormat("Date and Time:         {0}\n", DateTime.Now);
			sb.Append("Machine Name:          ");
			try {
				sb.Append(Environment.MachineName);
			}
			catch (Exception ex) {
				sb.Append(ex.Message);
			}
			sb.Append("\n");
			sb.AppendFormat("Process User:          {0}\n", ProcessIdentity());

			if (Settings.IsWebApp) {
				sb.AppendFormat("Remote User:           {0}\n", HttpContext.Current.Request.ServerVariables["REMOTE_USER"]);
				sb.AppendFormat("Remote Address:        {0}\n", HttpContext.Current.Request.ServerVariables["REMOTE_ADDR"]);
				sb.AppendFormat("Remote Host:           {0}\n", HttpContext.Current.Request.ServerVariables["REMOTE_HOST"]);
				sb.AppendFormat("URL:                   {0}\n\n", WebCurrentUrl());
			}
			else {
				sb.AppendFormat("IP Address:            {0}\n", Utilities.GetIPv4Address());
			}

			sb.Append("Application Domain:    ");
			try {
				sb.Append(AppDomain.CurrentDomain.FriendlyName);
			}
			catch (Exception ex) {
				sb.Append(ex.Message);
			}
			sb.Append("\n");
			sb.AppendFormat("NET Runtime version:   {0}\n", Environment.Version);

			if (includeStackTrace)
				sb.Append(EnhancedStackTrace());

			return sb.ToString();
		}

		#endregion System info

		#region Exception info

		private string ExceptionToString(Exception ex) {
			StringBuilder sb = new StringBuilder();

			sb.Append(ExceptionToStringPrivate(ex));
			// get ASP specific settings
			try {
				sb.Append(GetAspSettings());
			}
			catch (Exception e) {
				sb.Append(e.Message);
			}

			return sb.ToString();
		}

		private string ExceptionToStringPrivate(Exception ex, bool includeSysInfo) {
			StringBuilder sb = new StringBuilder();

			if (null != ex.InnerException) {
				// sometimes the original exception is wrapped in a more relevant outer exception
				// the detail exception is the "inner" exception
				// see http://msdn.microsoft.com/library/default.asp?url=/library/en-us/dnbda/html/exceptdotnet.asp
				//
				// don't return the outer root ASP exception; it is redundant.
				if (ex.GetType().ToString() == Settings.RootException || ex.GetType().ToString() == Settings.RootWsException) {
					return ExceptionToStringPrivate(ex.InnerException);
				}
				sb.AppendFormat("{0}\n(Outer Exception)", ExceptionToStringPrivate(ex.InnerException, false));
			}

			// get general system and app information
			// we only really want to do this on the outermost exception in the stack
			if (includeSysInfo) {
				sb.Append(SysInfoToString());
				sb.AppendFormat("{0}\n", AssemblyInfoToString(ex));
			}

			// get exception-specific information

			sb.Append("Exception Type:        ");
			try {
				sb.Append(ex.GetType().FullName);
			}
			catch (Exception e) {
				sb.Append(e.Message);
			}
			sb.Append("\n");

			sb.Append("Exception Message:     ");
			try {
				sb.Append(ex.Message);
			}
			catch (Exception e) {
				sb.Append(e.Message);
			}
			sb.Append("\n");

			sb.Append("Exception Source:      ");
			try {
				sb.Append(ex.Source);
			}
			catch (Exception e) {
				sb.Append(e.Message);
			}
			sb.Append("\n");

			sb.Append("Exception Target Site: ");
			try {
				sb.Append(ex.TargetSite.Name);
			}
			catch (Exception e) {
				sb.Append(e.Message);
			}
			sb.Append("\n");

			try {
				sb.Append(EnhancedStackTrace(ex));
			}
			catch (Exception e) {
				sb.Append(e.Message);
			}
			sb.Append("\n");

			return sb.ToString();
		}

		private string ExceptionToStringPrivate(Exception ex) {
			return ExceptionToStringPrivate(ex, true);
		}

		#endregion Exception info

		#region ASP Info

		private string GetAspSettings() {
			StringBuilder sb = new StringBuilder();

			if (Settings.IsWebApp) {
				const string suppressKeyPattern = "^ALL_HTTP|^ALL_RAW|VSDEBUGGER";

				sb.Append("---- ASP.NET Collections ----\n\n");
				sb.Append(HttpVarsToString(HttpContext.Current.Request.QueryString, "QueryString"));
				sb.Append(HttpVarsToString(HttpContext.Current.Request.Form, "Form"));
				sb.Append(HttpVarsToString(HttpContext.Current.Request.Cookies));
				sb.Append(HttpVarsToString(HttpContext.Current.Session));
				sb.Append(HttpVarsToString(HttpContext.Current.Cache));
				sb.Append(HttpVarsToString(HttpContext.Current.Application));
				sb.Append(HttpVarsToString(HttpContext.Current.Request.ServerVariables, "ServerVariables", true, suppressKeyPattern));
			}
			return sb.ToString();
		}

		#endregion ASP Info

		#region HTTPVars

		private static string HttpVarsToString(HttpCookieCollection c) {
			if (c.Count == 0) {
				return string.Empty;
			}

			StringBuilder sb = new StringBuilder();
			sb.Append("Cookies\n\n");

			foreach (string key in c) {
				AppendLine(sb, key, c[key].Value);
			}

			sb.Append("\n");

			return sb.ToString();
		}

		private static string HttpVarsToString(HttpApplicationState a) {
			if (a.Count == 0) {
				return string.Empty;
			}

			StringBuilder sb = new StringBuilder();
			sb.Append("Application\n\n");

			foreach (string key in a) {
				AppendLine(sb, key, a[key]);
			}

			sb.Append("\n");

			return sb.ToString();
		}

		private static string HttpVarsToString(Cache c) {
			if (c.Count == 0) {
				return string.Empty;
			}

			StringBuilder sb = new StringBuilder();
			sb.Append("Cache\n\n");

			foreach (DictionaryEntry de in c) {
				AppendLine(sb, (string)de.Key, de.Value);
			}

			sb.Append("\n");

			return sb.ToString();
		}

		private static string HttpVarsToString(HttpSessionState s) {
			if (null == s || 0 == s.Count) {
				return string.Empty;
			}

			StringBuilder sb = new StringBuilder();
			sb.Append("Session\n\n");

			foreach (string key in s) {
				AppendLine(sb, key, s[key]);
			}

			sb.Append("\n");

			return sb.ToString();
		}

		private string HttpVarsToString(NameValueCollection nvc, string title, bool suppressEmpty, string suppressKeyPattern) {
			if (!nvc.HasKeys()) {
				return string.Empty;
			}

			StringBuilder sb = new StringBuilder();
			sb.AppendFormat("{0}\n\n", title);

			foreach (string key in nvc) {
				bool display = true;

				if (suppressEmpty) {
					display = !string.IsNullOrEmpty(nvc[key]);
				}

				if (key == Settings.ViewStateKey) {
					ViewState = nvc[key];
					display = true;
				}

				if (display && !string.IsNullOrEmpty(suppressKeyPattern)) {
					display = !Regex.IsMatch(key, suppressKeyPattern);
				}

				if (display) {
					AppendLine(sb, key, nvc[key]);
				}
			}

			sb.Append("\n");

			return sb.ToString();
		}

		private string HttpVarsToString(NameValueCollection nvc, string title) {
			return HttpVarsToString(nvc, title, false, string.Empty);
		}

		// Remove if not used...
		private string HttpVarsToString(NameValueCollection nvc, string title, bool suppressEmpty) {
			return HttpVarsToString(nvc, title, suppressEmpty, string.Empty);
		}

		private string HttpVarsToString(NameValueCollection nvc, string title, string suppressKeyPattern) {
			return HttpVarsToString(nvc, title, true, suppressKeyPattern);
		}

		#endregion HTTPVars

		#region Custom string handlers

		private static string AppendLine(StringBuilder sb, string key, object value) {
			string val;
			if (null == value) {
				val = "(Null)";
			}
			else {
				try {
					val = value.ToString();
				}
				catch {
					val = string.Format("({0})", value.GetType());
				}
			}

			AppendLine(sb, key, val);

			return sb.ToString();
		}

		private static string AppendLine(StringBuilder sb, string key, string value) {
			sb.AppendFormat("    {0, -30} {1}\n", key, value);
			return sb.ToString();
		}

		#endregion Custom string handlers
	}
}