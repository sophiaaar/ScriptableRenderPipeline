﻿using System;
using UnityEngine;

namespace UnityEditor.ShaderGraph
{
    [Serializable]
    class StickyNoteData : ISerializationCallbackReceiver, IGroupItem
    {
        [NonSerialized]
        Guid m_Guid;

        public Guid guid => m_Guid;

        [SerializeField]
        string m_GuidSerialized;

        [SerializeField]
        string m_Title;

        public string title
        {
            get => m_Title;
            set => m_Title = value;
        }

        [SerializeField]
        string m_Content;

        public string content
        {
            get => m_Content;
            set => m_Content = value;
        }

        [SerializeField]
        int m_TextSize;

        public int textSize
        {
            get => m_TextSize;
            set => m_TextSize = value;
        }

        [SerializeField]
        int m_Theme;

        public int theme
        {
            get => m_Theme;
            set => m_Theme = value;
        }

        [SerializeField]
        Vector2 m_Position;

        public Vector2 position
        {
            get => m_Position;
            set => m_Position = value;
        }

        [SerializeField]
        string m_GroupGuidSerialized;

        [NonSerialized]
        Guid m_GroupGuid;

        public Guid groupGuid
        {
            get { return m_GroupGuid; }
            set { m_GroupGuid = value; }
        }

        public StickyNoteData(string title, string content, Vector2 position)
        {
            m_Guid = Guid.NewGuid();
            m_Title = title;
            m_Position = position;
            m_Content = content;
        }

        public void OnBeforeSerialize()
        {
            m_GuidSerialized = guid.ToString();
            m_GroupGuidSerialized = groupGuid.ToString();
        }

        public void OnAfterDeserialize()
        {
            if (!string.IsNullOrEmpty(m_GuidSerialized))
            {
                m_Guid = new Guid(m_GuidSerialized);
            }

            if (!string.IsNullOrEmpty(m_GroupGuidSerialized))
            {
                m_GroupGuid = new Guid(m_GroupGuidSerialized);
            }
        }
    }
}

