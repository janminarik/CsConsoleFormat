﻿using System.Collections.Generic;
using System.Reflection;
using Alba.CsConsoleFormat.Framework.Text;
using Alba.CsConsoleFormat.Markup;

namespace Alba.CsConsoleFormat
{
    public abstract class Element
    {
        private ContainerElement _parent;
        private object _dataContext;
        private IDictionary<PropertyInfo, GetExpression> _getters;

        internal GeneratorElement Generator { get; set; }

        public ContainerElement Parent
        {
            get { return _parent; }
            internal set
            {
                if (_parent == value)
                    return;
                _parent = value;
                if (Generator == null)
                    DataContext = _parent.DataContext;
            }
        }

        public object DataContext
        {
            get { return _dataContext; }
            set
            {
                if (_dataContext == value)
                    return;
                _dataContext = value;
                UpdateDataContext();
            }
        }

        protected virtual void UpdateDataContext ()
        {
            if (_getters == null)
                return;
            foreach (KeyValuePair<PropertyInfo, GetExpression> getter in _getters)
                getter.Key.SetValue(this, getter.Value.GetValue(_dataContext));
        }

        public void Bind (PropertyInfo prop, GetExpression getter)
        {
            if (_getters == null)
                _getters = new SortedList<PropertyInfo, GetExpression>();
            _getters[prop] = getter;
        }

        public Element Clone ()
        {
            return (Element)MemberwiseClone();
        }

        public override string ToString ()
        {
            return "{0}: DC={1}".Fmt(GetType().Name, DataContext);
        }
    }
}