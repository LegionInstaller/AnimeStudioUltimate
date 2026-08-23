using System;
using System.Windows.Forms;

namespace AnimeStudio.GUI
{
    class GUILogger : ILogger
    {
        public bool ShowErrorMessage = true;
        private readonly Action<string> action;
        private readonly Action<string> errorAction;

        public bool Silent { get; set; }
        public LoggerEvent Flags { get; set; }

        /// <param name="action">Shows a status message. Must not block the caller.</param>
        /// <param name="errorAction">
        /// Shows an error to the user. Loading reports from several threads at once, so this has
        /// to put the dialog on the UI thread instead of opening one per worker thread.
        /// </param>
        public GUILogger(Action<string> action, Action<string> errorAction = null)
        {
            this.action = action;
            this.errorAction = errorAction;
        }

        public void Log(LoggerEvent loggerEvent, string message)
        {
            if (!Flags.HasFlag(loggerEvent) || Silent)
                return;

            switch (loggerEvent)
            {
                case LoggerEvent.Error:
                    if (ShowErrorMessage)
                    {
                        if (errorAction != null)
                        {
                            errorAction(message);
                        }
                        else
                        {
                            MessageBox.Show(message);
                        }
                    }
                    break;
                default:
                    action(message);
                    break;
            }
        }
    }
}
